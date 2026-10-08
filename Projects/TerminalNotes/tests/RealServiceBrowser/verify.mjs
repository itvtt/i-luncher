#!/usr/bin/env node

// Dependency-free Chromium CDP smoke harness for a real localhost service.
// It deliberately does not start a service, create an app, or edit production files.
import { mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { basename, join, resolve } from "node:path";
import { tmpdir } from "node:os";
import { execFile, spawn } from "node:child_process";
import { promisify } from "node:util";
import { pathToFileURL } from "node:url";

const execFileAsync = promisify(execFile);

const EDGE = process.env.EDGE_PATH ||
  "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const DEFAULT_OUTPUT = resolve("tests", "artifacts", "RealServiceBrowser");
const MAX_TEXT = 4000;
const COMMAND_TIMEOUT = 15_000;

function usage(message) {
  if (message) console.error(`Error: ${message}`);
  console.error("Usage: node verify.mjs --url http://127.0.0.1:PORT/ --output <artifact-folder> [--script <module.mjs>]");
  process.exitCode = 2;
}

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i++) {
    const value = argv[i];
    if (!value.startsWith("--")) return usage(`Unexpected argument: ${value}`);
    const key = value.slice(2);
    if (key === "help") {
      usage();
      process.exit(0);
    }
    if (!argv[i + 1] || argv[i + 1].startsWith("--")) return usage(`Missing value for --${key}`);
    args[key] = argv[++i];
  }
  if (!args.url) return usage("--url is required");
  return args;
}

function validateLocalUrl(value) {
  let url;
  try { url = new URL(value); } catch { throw new Error("--url must be an absolute HTTP URL"); }
  if (url.protocol !== "http:" || !["localhost", "127.0.0.1"].includes(url.hostname))
    throw new Error("Only http://localhost or http://127.0.0.1 URLs are allowed");
  return url.toString();
}

function sanitize(value) {
  let text = String(value ?? "");
  text = text.replace(/\b(Bearer|Basic)\s+[A-Za-z0-9+/=_\-.]+/gi, "$1 [redacted]");
  text = text.replace(/((?:authorization|api[_-]?key|access[_-]?token|refresh[_-]?token|password|secret)\s*[:=]\s*)(["']?)[^\s,"'}]+\2/gi, "$1[redacted]");
  text = text.replace(/(https?:\/\/[^\s?#]+)[?#][^\s]+/gi, "$1?[redacted]");
  return text.length > MAX_TEXT ? `${text.slice(0, MAX_TEXT)}…` : text;
}

function sleep(milliseconds) { return new Promise(resolvePromise => setTimeout(resolvePromise, milliseconds)); }

async function waitFor(predicate, timeout, description) {
  const deadline = Date.now() + timeout;
  let lastError;
  while (Date.now() < deadline) {
    try {
      const result = await predicate();
      if (result) return result;
    } catch (error) { lastError = error; }
    await sleep(100);
  }
  throw new Error(`${description} timed out${lastError ? `: ${lastError.message}` : ""}`);
}

async function getJson(url) {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`CDP endpoint HTTP ${response.status}: ${url}`);
  return response.json();
}

async function readDevToolsActivePort(profile) {
  const file = join(profile, "DevToolsActivePort");
  const text = await readFile(file, "utf8");
  const lines = text.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
  const port = Number(lines[0]);
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error("Invalid DevToolsActivePort value");
  return { port, browserPath: lines[1] ?? "" };
}

class CdpConnection {
  constructor(socket) {
    this.socket = socket;
    this.nextId = 1;
    this.pending = new Map();
    this.listeners = new Map();
    socket.addEventListener("message", event => {
      let message;
      try { message = JSON.parse(String(event.data)); } catch { return; }
      if (message.id && this.pending.has(message.id)) {
        const pending = this.pending.get(message.id);
        this.pending.delete(message.id);
        if (message.error) pending.reject(new Error(`${message.error.code}: ${message.error.message}`));
        else pending.resolve(message.result ?? {});
        return;
      }
      for (const listener of this.listeners.get(message.method) ?? []) listener(message.params ?? {});
    });
    socket.addEventListener("close", () => {
      for (const pending of this.pending.values()) pending.reject(new Error("CDP socket closed"));
      this.pending.clear();
    });
  }

  on(method, listener) {
    const listeners = this.listeners.get(method) ?? [];
    listeners.push(listener);
    this.listeners.set(method, listeners);
    return () => this.listeners.set(method, (this.listeners.get(method) ?? []).filter(item => item !== listener));
  }

  waitFor(method, timeout = COMMAND_TIMEOUT) {
    return new Promise((resolvePromise, reject) => {
      const remove = this.on(method, parameters => { clearTimeout(timer); remove(); resolvePromise(parameters); });
      const timer = setTimeout(() => { remove(); reject(new Error(`Timed out waiting for ${method}`)); }, timeout);
    });
  }

  send(method, params = {}, timeout = COMMAND_TIMEOUT) {
    const id = this.nextId++;
    return new Promise((resolvePromise, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Timed out waiting for CDP ${method}`));
      }, timeout);
      this.pending.set(id, {
        resolve: result => { clearTimeout(timer); resolvePromise(result); },
        reject: error => { clearTimeout(timer); reject(error); }
      });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }
}

async function openCdpTarget(port) {
  const targets = await getJson(`http://127.0.0.1:${port}/json/list`);
  const target = targets.find(item => item.type === "page" && item.webSocketDebuggerUrl);
  if (!target) throw new Error("Edge did not expose a page target");
  const socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolvePromise, reject) => {
    const timer = setTimeout(() => reject(new Error("Timed out connecting to Edge CDP")), COMMAND_TIMEOUT);
    socket.addEventListener("open", () => { clearTimeout(timer); resolvePromise(); }, { once: true });
    socket.addEventListener("error", error => { clearTimeout(timer); reject(error); }, { once: true });
  });
  return new CdpConnection(socket);
}

async function evaluate(cdp, expression, options = {}) {
  const result = await cdp.send("Runtime.evaluate", {
    expression: String(expression),
    awaitPromise: true,
    returnByValue: true,
    userGesture: options.userGesture ?? true,
    includeCommandLineAPI: false
  });
  if (result.exceptionDetails) {
    throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text || "Runtime.evaluate failed");
  }
  return result.result?.value;
}

function evaluateForScript(cdp, expression, values = []) {
  if (typeof expression === "function") {
    const argumentsText = values.map(value => JSON.stringify(value)).join(",");
    return evaluate(cdp, `(${expression.toString()})(${argumentsText})`);
  }
  return evaluate(cdp, String(expression));
}

async function click(cdp, selector) {
  const rect = await evaluate(cdp, `(() => { const element = document.querySelector(${JSON.stringify(selector)}); if (!element) return null; const r = element.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
  if (!rect) throw new Error(`Element not found: ${selector}`);
  await cdp.send("Input.dispatchMouseEvent", { type: "mouseMoved", x: rect.x, y: rect.y });
  await cdp.send("Input.dispatchMouseEvent", { type: "mousePressed", button: "left", clickCount: 1, x: rect.x, y: rect.y });
  await cdp.send("Input.dispatchMouseEvent", { type: "mouseReleased", button: "left", clickCount: 1, x: rect.x, y: rect.y });
}

async function key(cdp, value, options = {}) {
  const keyValue = typeof value === "string" ? value : value.key;
  const code = typeof value === "string" ? ({ Enter: "Enter", Escape: "Escape", Tab: "Tab", " ": "Space" }[value] ?? value) : (value.code ?? value.key);
  const text = typeof value === "string" && value.length === 1 ? value : (value.text ?? undefined);
  const modifiers = options.modifiers ?? (typeof value === "object" ? value.modifiers ?? 0 : 0);
  await cdp.send("Input.dispatchKeyEvent", { type: "rawKeyDown", key: keyValue, code, text, modifiers });
  await cdp.send("Input.dispatchKeyEvent", { type: "keyUp", key: keyValue, code, modifiers });
}

async function captureScreenshot(cdp, output, name = "service.png") {
  const safeName = basename(name).replace(/[^A-Za-z0-9._-]/g, "_");
  const path = join(output, safeName.toLowerCase().endsWith(".png") ? safeName : `${safeName}.png`);
  const result = await cdp.send("Page.captureScreenshot", { format: "png", fromSurface: true });
  await writeFile(path, Buffer.from(result.data, "base64"));
  return path;
}

async function startEdge(profile) {
  if (!existsSync(EDGE)) throw new Error(`Edge executable not found: ${EDGE}`);
  const child = spawn(EDGE, [
    "--headless=new", "--window-size=1280,960", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
    "--disable-background-networking", "--disable-component-update", "--hide-scrollbars",
    "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1", `--user-data-dir=${profile}`,
    "about:blank"
  ], { windowsHide: true, stdio: "ignore" });
  try {
    const active = await waitFor(async () => {
      if (child.exitCode !== null) throw new Error(`Edge exited before CDP startup (${child.exitCode})`);
      try { return await readDevToolsActivePort(profile); } catch { return false; }
    }, 20_000, "Edge DevToolsActivePort");
    return { child, ...active };
  } catch (error) {
    await stopEdge(child);
    throw error;
  }
}

async function stopEdge(child) {
  if (!child || child.exitCode !== null) return;
  if (process.platform === "win32" && child.pid) {
    try {
      // The PID is the process created above; /T only includes its Edge children.
      await execFileAsync("taskkill", ["/PID", String(child.pid), "/T", "/F"], { windowsHide: true });
    } catch { /* the browser may already have exited */ }
  } else {
    try { child.kill(); } catch { /* only the process created by this harness */ }
  }
  await Promise.race([new Promise(resolvePromise => child.once("exit", resolvePromise)), sleep(2_000)]);
}

async function run() {
  const args = parseArgs(process.argv.slice(2));
  if (!args) return;
  const url = validateLocalUrl(args.url);
  const output = resolve(args.output || DEFAULT_OUTPUT);
  await mkdir(output, { recursive: true });
  const script = args.script ? resolve(args.script) : null;
  if (script && !existsSync(script)) throw new Error(`Test module not found: ${script}`);
  const profile = await mkdtemp(join(tmpdir(), "TerminalNotes-RealServiceBrowser-"));
  let edge;
  let cdp;
  const startedAt = new Date().toISOString();
  const consoleErrors = [];
  const httpFailures = [];
  let screenshot = null;
  let scriptResult = null;
  let dom = null;
  let failure = null;
  try {
    edge = await startEdge(profile);
    const protocol = await getJson(`http://127.0.0.1:${edge.port}/json/protocol`);
    if (!protocol.domains?.some(domain => domain.domain === "Page") || !protocol.domains?.some(domain => domain.domain === "Runtime"))
      throw new Error("Edge CDP protocol does not expose required Page/Runtime domains");
    cdp = await openCdpTarget(edge.port);
    cdp.on("Runtime.consoleAPICalled", event => {
      if (["error", "assert"].includes(event.type))
        consoleErrors.push(sanitize((event.args ?? []).map(arg => arg.value ?? arg.description ?? "").join(" ")));
    });
    cdp.on("Runtime.exceptionThrown", event => consoleErrors.push(sanitize(event.exceptionDetails?.text || event.exceptionDetails?.exception?.description || "Uncaught page exception")));
    cdp.on("Network.responseReceived", event => {
      if (event.response.status >= 400) httpFailures.push({ status: event.response.status, url: sanitize(event.response.url) });
    });
    cdp.on("Network.loadingFailed", event => httpFailures.push({ error: sanitize(event.errorText || "loading failed"), requestId: event.requestId }));
    await cdp.send("Page.enable");
    await cdp.send("Runtime.enable");
    await cdp.send("Network.enable");
    const loaded = cdp.waitFor("Page.loadEventFired", 30_000).catch(() => null);
    await cdp.send("Page.navigate", { url });
    await loaded;
    await sleep(250);
    // A localhost start URL must not silently redirect this harness to the internet.
    validateLocalUrl(String(await evaluate(cdp, "location.href")));
    if (script) {
      const imported = await import(`${pathToFileURL(script).href}?run=${Date.now()}`);
      const runner = imported.default ?? imported.run;
      if (typeof runner !== "function") throw new Error("Test module must export default or run(context)");
      scriptResult = await runner({
        url,
        output,
        evaluate: (expression, ...values) => evaluateForScript(cdp, expression, values),
        key: (value, options) => key(cdp, value, options),
        click: selector => click(cdp, selector),
        captureScreenshot: name => captureScreenshot(cdp, output, name),
        waitFor: (predicate, timeout, description) => waitFor(
          () => typeof predicate === "function"
            ? evaluateForScript(cdp, predicate)
            : evaluate(cdp, String(predicate)),
          timeout,
          description)
      });
    }
    screenshot = await captureScreenshot(cdp, output);
    dom = await evaluate(cdp, `(() => ({ title: document.title, url: location.href, bodyText: (document.body?.innerText || "").slice(0, ${MAX_TEXT}), elementCount: document.querySelectorAll("*").length }))()`);
  } catch (error) {
    failure = sanitize(error?.stack || error?.message || error);
    try { if (cdp) screenshot = await captureScreenshot(cdp, output, "failure.png"); } catch { /* best effort evidence */ }
  } finally {
    try { cdp?.socket.close(); } catch { }
    await stopEdge(edge?.child);
  }

  const result = {
    ok: !failure && consoleErrors.length === 0 && httpFailures.length === 0,
    startedAt,
    finishedAt: new Date().toISOString(),
    url,
    edgePath: EDGE,
    edgePort: edge?.port ?? null,
    script: script ?? null,
    dom,
    consoleErrors,
    httpFailures,
    screenshot,
    scriptResult: scriptResult === undefined ? null : scriptResult,
    error: failure,
    profile
  };
  const resultPath = join(output, "result.json");
  await writeFile(resultPath, JSON.stringify(result, null, 2) + "\n", "utf8");
  console.log(JSON.stringify({ ...result, resultPath }, null, 2));
  if (!result.ok) process.exitCode = 1;
}

run().catch(error => { console.error(sanitize(error?.stack || error?.message || error)); process.exitCode = 1; });
