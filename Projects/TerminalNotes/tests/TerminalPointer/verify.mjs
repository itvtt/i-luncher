// Exercises the shipped xterm bundle in headless Edge; no terminal process or model is started.
import assert from 'node:assert/strict';
import { mkdtemp, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(check, description) {
  const end = Date.now() + 15000;
  while (Date.now() < end) {
    try { const value = await check(); if (value) return value; } catch { }
    await sleep(75);
  }
  throw new Error('Timed out: ' + description);
}
const profile = await mkdtemp(join(tmpdir(), 'TerminalNotes-Pointer-'));
// This isolated renderer loads only the repository's local assets. The outer Windows
// execution sandbox prevents a nested Chromium renderer sandbox from starting.
const edge = spawn(process.env.EDGE_PATH || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', [
  '--headless=new', '--no-sandbox', '--window-size=900,600', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  '--disable-background-networking', '--disable-component-update', '--remote-debugging-port=0',
  '--remote-debugging-address=127.0.0.1', `--user-data-dir=${profile}`, 'about:blank'
], { windowsHide: true, stdio: 'ignore' });
let socket, closeBrowser;
try {
  const port = await until(async () => Number((await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]), 'headless browser');
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  socket = new WebSocket(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  let nextId = 0;
  const pending = new Map();
  socket.onmessage = event => {
    const message = JSON.parse(event.data);
    const callback = pending.get(message.id);
    if (!callback) return;
    pending.delete(message.id);
    message.error ? callback.reject(new Error(message.error.message)) : callback.resolve(message.result);
  };
  const cdp = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++nextId;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('CDP timeout: ' + method)); }, 10000);
    pending.set(id, { resolve: value => { clearTimeout(timer); resolve(value); }, reject: error => { clearTimeout(timer); reject(error); } });
    socket.send(JSON.stringify({ id, method, params }));
  });
  closeBrowser = () => cdp('Browser.close');
  const evaluate = async expression => {
    const result = await cdp('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result.value;
  };
  await cdp('Runtime.enable');
  await cdp('Page.enable');
  await cdp('Page.addScriptToEvaluateOnNewDocument', { source: `window.hostMessages=[]; window.chrome.webview={postMessage:m=>hostMessages.push(m),addEventListener:()=>{}};` });
  const page = pathToFileURL(fileURLToPath(new URL('../../Assets/terminal.html', import.meta.url))).href;
  await cdp('Page.navigate', { url: page });
  await until(() => evaluate(`typeof terminal !== 'undefined' && terminal.cols > 20`), 'terminal initialization');
  await evaluate(String.raw`new Promise(resolve=>terminal.write('abcdefghijklmnopqrstuvwxyz 0123456789\r\nSECOND LINE abcdefghijklmnopqrstuvwxyz\r\nTHIRD LINE abcdefghijklmnopqrstuvwxyz',resolve))`);
  const rect = await evaluate(`(()=>{const r=terminal.element.querySelector('.xterm-screen').getBoundingClientRect();return {x:r.left,y:r.top,w:r.width/terminal.cols,h:r.height/terminal.rows}})()`);
  const point = (column, row = 0) => ({ x: rect.x + rect.w * (column + .5), y: rect.y + rect.h * (row + .5) });
  const mouse = (type, position, buttons, button = 'left') => cdp('Input.dispatchMouseEvent', { type, ...position, button, buttons, clickCount: type === 'mouseMoved' ? 0 : 1 });
  const begin = async () => {
    await evaluate(`terminal.clearSelection()`);
    await mouse('mousePressed', point(1), 1);
    await mouse('mouseMoved', point(8), 1);
    assert.ok(await evaluate('terminal.getSelection().length') > 0, 'drag selects terminal text');
  };
  const selection = () => evaluate('terminal.getSelection()');
  const verifyStopped = async (label, finish) => {
    await begin();
    await finish();
    const stopped = await selection();
    assert.ok(stopped.length > 0, label + ' preserves text for copy');
    // Verify cleanup independently of the released-button recovery below.
    await evaluate(`document.dispatchEvent(new MouseEvent('mousemove',{bubbles:true,buttons:1,clientX:${point(18, 1).x},clientY:${point(18, 1).y}}))`);
    assert.equal(await selection(), stopped, label + ' removes the old drag listener');
    // WebView can miss mouse-up outside its HWND. DOM movement still reports buttons=0 on return.
    await evaluate(`document.dispatchEvent(new MouseEvent('mousemove',{bubbles:true,buttons:0,clientX:${point(24, 1).x},clientY:${point(24, 1).y}}))`);
    await sleep(80);
    assert.equal(await selection(), stopped, label + ' stops selection extension');
    await mouse('mouseReleased', point(24, 1), 0);
    console.log('PASS ' + label);
  };
  await verifyStopped('normal mouse-up preserves the completed selection', () => mouse('mouseReleased', point(8), 0));
  await verifyStopped('lost mouse-up is recovered on a released-button move', () => evaluate(`document.dispatchEvent(new MouseEvent('mousemove',{bubbles:true,buttons:0,clientX:${point(8).x},clientY:${point(8).y}}))`));
  await verifyStopped('pointer-up fallback ends a gesture with a missing mouse-up', async () => {
    await evaluate(`terminal.element.dispatchEvent(new PointerEvent('pointerup',{bubbles:true,pointerId:1,pointerType:'mouse',button:0,buttons:0,clientX:${point(8).x},clientY:${point(8).y}}))`);
    await sleep(20);
  });
  await verifyStopped('window blur finishes selection without clearing copied text', () => evaluate(`window.dispatchEvent(new Event('blur'))`));
  await verifyStopped('pointer cancellation finishes selection', () => evaluate(`terminal.element.dispatchEvent(new PointerEvent('pointercancel',{bubbles:true,pointerId:1,pointerType:'mouse'}))`));
  await verifyStopped('lost pointer capture finishes selection', () => evaluate(`terminal.element.dispatchEvent(new PointerEvent('lostpointercapture',{bubbles:true,pointerId:1,pointerType:'mouse'}))`));
  await begin();
  const first = await selection();
  await mouse('mouseMoved', point(16), 1);
  assert.ok((await selection()).length > first.length, 'normal held-button movement continues selecting');
  await mouse('mouseReleased', point(16), 0);
  console.log('PASS normal dragging still extends text selection');
  await evaluate(String.raw`new Promise(resolve=>terminal.write('\x1b[?1002h\x1b[?1006h',resolve))`);
  await evaluate('hostMessages.length=0');
  await mouse('mousePressed', point(2, 1), 1);
  await mouse('mouseMoved', point(4, 1), 1);
  await mouse('mouseReleased', point(4, 1), 0);
  const reports = await evaluate(`hostMessages.filter(m=>m.type==='input').map(m=>m.data).join('')`);
  assert.match(reports, /\x1b\[<0;\d+;\d+M/, 'TUI receives mouse down');
  assert.match(reports, /\x1b\[<0;\d+;\d+m/, 'TUI receives mouse up');
  console.log('PASS TUI mouse reporting still receives presses, moves and releases');
  await evaluate('hostMessages.length=0');
  await mouse('mousePressed', point(2, 1), 1);
  await evaluate(`window.dispatchEvent(new Event('blur'))`);
  const lostReleaseReports = await evaluate(`hostMessages.filter(m=>m.type==='input').map(m=>m.data).join('')`);
  assert.match(lostReleaseReports, /\x1b\[<0;\d+;\d+m/, 'lost TUI mouse-up is sent through the existing xterm protocol');
  await evaluate('hostMessages.length=0');
  await evaluate(`document.dispatchEvent(new MouseEvent('mousemove',{bubbles:true,buttons:0,clientX:${point(12, 1).x},clientY:${point(12, 1).y}}))`);
  assert.equal(await evaluate(`hostMessages.filter(m=>m.type==='input').length`), 0, 'TUI receives no phantom held-button move after blur');
  await mouse('mouseReleased', point(12, 1), 0);
  console.log('PASS a lost TUI release ends tracking without phantom drag input');
  console.log('TERMINAL POINTER CHECKS PASSED');
} finally {
  try { await closeBrowser?.(); } catch { }
  socket?.close();
  if (edge.exitCode === null && edge.pid) {
    try { await promisify(execFile)('taskkill', ['/PID', String(edge.pid), '/T', '/F'], { windowsHide: true, timeout: 5000 }); } catch { }
    edge.unref();
  }
}
