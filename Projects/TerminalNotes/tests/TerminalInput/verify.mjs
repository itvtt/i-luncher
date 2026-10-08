// Exercises the shipped xterm bundle in headless Edge; no terminal process or CLI is started.
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
    await sleep(50);
  }
  throw new Error('Timed out: ' + description);
}
const profile = await mkdtemp(join(tmpdir(), 'TerminalNotes-Input-'));
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
  await cdp('Page.addScriptToEvaluateOnNewDocument', { source: `
    window.hostMessages=[]; window.hostHandlers=[];
    window.chrome.webview={postMessage:m=>hostMessages.push(m),addEventListener:(type,handler)=>hostHandlers.push(handler)};
    window.postHostMessage=m=>hostHandlers.forEach(handler=>handler({data:m}));
  ` });
  const page = pathToFileURL(fileURLToPath(new URL('../../Assets/terminal.html', import.meta.url))).href;
  await cdp('Page.navigate', { url: page });
  await until(() => evaluate(`typeof terminal !== 'undefined' && terminal.cols > 20`), 'terminal initialization');
  await evaluate(`new Promise(resolve=>terminal.write('xterm input regression fixture\\r\\n'.repeat(240),resolve))`);
  await evaluate('terminal.focus()');
  assert.equal(await evaluate('document.activeElement===terminal.textarea'), true, 'xterm textarea receives focus');

  async function typeText(value) {
    for (const character of value) {
      const upper = character.toUpperCase();
      const code = 'Key' + upper;
      const keyCode = upper.charCodeAt(0);
      await cdp('Input.dispatchKeyEvent', { type: 'keyDown', key: character, code, windowsVirtualKeyCode: keyCode, nativeVirtualKeyCode: keyCode, text: character, unmodifiedText: character });
      await cdp('Input.dispatchKeyEvent', { type: 'keyUp', key: character, code, windowsVirtualKeyCode: keyCode, nativeVirtualKeyCode: keyCode });
    }
  }
  const rail = await evaluate(`(()=>{const r=verticalScrollbar.getBoundingClientRect();return {x:r.right-6,y:r.bottom-24}})()`);
  await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', ...rail, button: 'left', buttons: 1, clickCount: 1 });
  await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', ...rail, button: 'left', buttons: 0, clickCount: 1 });
  await sleep(50);
  assert.equal(await evaluate('document.activeElement===terminal.textarea'), true, 'scrollbar pointer click restores the previously active xterm');
  await typeText('q');
  assert.equal(await evaluate(`hostMessages.filter(m=>m.type==='input').map(m=>m.data).join('')`), 'q', 'keyboard input still reaches xterm exactly once after rail click');

  await evaluate('hostMessages.length=0');
  await typeText('aab');
  await evaluate(`postHostMessage({type:'output',data:'OUT_'.repeat(1500)});`);
  await evaluate(`postHostMessage({type:'theme',background:'#101820',foreground:'#f0f0f0',cursor:'#a0f0d0'});`);
  await evaluate(`terminal.resize(terminal.cols-1,terminal.rows);`);
  await typeText('cc');
  await evaluate(`postHostMessage({type:'sendText',id:71,data:'auto',submit:true});`);
  await until(() => evaluate(`hostMessages.some(m=>m.type==='sendTextAck'&&m.id===71)`), 'programmatic send acknowledgement');
  await typeText('z');
  const inputs = await evaluate(`hostMessages.filter(m=>m.type==='input').map(m=>({data:m.data,programmatic:!!m.programmatic,sendId:m.sendId??null}))`);
  assert.equal(inputs.filter(item=>!item.programmatic).map(item=>item.data).join(''), 'aabccz', 'keyboard characters preserve order and repeats across output, theme, resize and sendText activity');
  assert.equal(inputs.filter(item=>item.programmatic).map(item=>item.data).join(''), 'auto\r', 'programmatic paste and submit are emitted once in order');
  assert.deepEqual(inputs.filter(item=>item.programmatic).map(item=>item.sendId), [71, 71], 'programmatic send id is preserved for text and submit');
  console.log('PASS keyboard input order and exact-once behavior across terminal activity');
  console.log('PASS pointer scrollbar focus recovery while preserving xterm keyboard input');
  console.log('NOTE synthetic browser keys exercised; actual IME composition is not simulated');
  console.log('TERMINAL INPUT CHECKS PASSED');
} finally {
  try { await closeBrowser?.(); } catch { }
  socket?.close();
  if (edge.exitCode === null && edge.pid) {
    try { await promisify(execFile)('taskkill', ['/PID', String(edge.pid), '/T', '/F'], { windowsHide: true, timeout: 5000 }); } catch { }
    edge.unref();
  }
}
