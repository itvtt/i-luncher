import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const root = new URL('../../', import.meta.url);
const js = await readFile(new URL('Assets/opencode.js', root), 'utf8');
const css = await readFile(new URL('Assets/opencode.css', root), 'utf8');
const html = await readFile(new URL('Assets/opencode.html', root), 'utf8');
assert.doesNotMatch(js, /\binnerHTML\b|\bouterHTML\b|\bfetch\s*\(|XMLHttpRequest|WebSocket\s*\(/, 'renderer has no HTML injection or network APIs');
assert.match(html, /default-src 'none'.*connect-src 'none'/, 'page CSP blocks all network and default resources');
assert.match(css, /@media \(max-width: 620px\)/, 'view has a narrow viewport layout');
assert.doesNotMatch(js, /addEventListener\s*\(\s*['"]resize['"]|ResizeObserver|nativeResize/, 'viewport sizing does not depend on a host resize bridge');

class FakeElement {
  constructor(tagName = 'div') {
    this.tagName = tagName.toUpperCase(); this.children = []; this.childNodes = this.children;
    this.dataset = {}; this.attributes = {}; this.listeners = {}; this.className = '';
    this.textContent = ''; this.value = ''; this.disabled = false; this.hidden = false;
    this.checked = false; this.type = ''; this.name = ''; this.parentNode = null;
    this.selectionStart = 0; this.selectionEnd = 0;
  }
  appendChild(node) {
    if (node.parentNode) node.remove();
    this.children.push(node); node.parentNode = this; return node;
  }
  replaceChildren(...nodes) {
    for (const child of this.children) child.parentNode = null;
    this.children.length = 0; for (const node of nodes) this.appendChild(node);
  }
  replaceWith(node) {
    if (!this.parentNode) return;
    const siblings = this.parentNode.children; const index = siblings.indexOf(this);
    if (node.parentNode) node.remove();
    siblings[index] = node; node.parentNode = this.parentNode; this.parentNode = null;
  }
  remove() {
    if (!this.parentNode) return;
    const siblings = this.parentNode.children; const index = siblings.indexOf(this);
    if (index >= 0) siblings.splice(index, 1); this.parentNode = null;
  }
  addEventListener(type, listener) { (this.listeners[type] ||= []).push(listener); }
  dispatch(type, event = {}) {
    event.target ||= this; event.preventDefault ||= (() => { event.defaultPrevented = true; });
    for (const listener of this.listeners[type] || []) listener(event);
    return event;
  }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  focus() { this.focused = true; document.activeElement = this; }
  setSelectionRange(start, end) { this.selectionStart = start; this.selectionEnd = end; }
  querySelectorAll(selector) {
    const results = [];
    const matches = node => selector === 'button' ? node.tagName === 'BUTTON' : false;
    const visit = node => { for (const child of node.children) { if (matches(child)) results.push(child); visit(child); } };
    visit(this); return results;
  }
  get innerText() { return this.textContent + this.children.map(child => child.innerText).join(''); }
}

const ids = ['conversation','messages','empty-state','permissions','questions','composer','send-button','stop-button',
  'status-label','status-dot','session-title','notice','session-select','model-wrap','model-select','agent-wrap',
  'agent-select','new-button','refresh-button'];
const elements = Object.fromEntries(ids.map(id => [id, new FakeElement(id.includes('select') ? 'select' : id === 'composer' ? 'textarea' : 'div')]));
elements.modelWrap = elements['model-wrap']; elements.agentWrap = elements['agent-wrap'];
elements.model = elements['model-select']; elements.agent = elements['agent-select'];
const hostMessages = []; const hostListeners = [];
const webview = {
  postMessage: message => hostMessages.push(message),
  addEventListener: (_type, listener) => hostListeners.push(listener)
};
const documentListeners = {};
let pageSelection = '';
const selection = { toString: () => pageSelection, removeAllRanges: () => { pageSelection = ''; }, addRange: range => { pageSelection = range.target.innerText; } };
const document = {
  activeElement: null,
  getElementById: id => elements[id],
  createElement: tag => new FakeElement(tag),
  createRange: () => ({ selectNodeContents(node) { this.target = node; } }),
  addEventListener: (type, listener) => { (documentListeners[type] ||= []).push(listener); }
};
const window = { chrome: { webview }, getSelection: () => selection };
const context = vm.createContext({ document, window, setTimeout: () => 1, clearTimeout: () => {}, console });
vm.runInContext(js, context, { filename: fileURLToPath(new URL('Assets/opencode.js', root)) });
const sent = () => hostMessages.filter(message => message.type === 'nativeSend');
const postHost = message => hostListeners.forEach(listener => listener({ data: message }));
const findAll = (node, predicate, output = []) => {
  for (const child of node.children) { if (predicate(child)) output.push(child); findAll(child, predicate, output); }
  return output;
};
const click = node => node.dispatch('click');

assert.equal(JSON.stringify(hostMessages[0]), JSON.stringify({ type: 'nativeReady' }), 'view announces native readiness');
postHost({ type: 'nativeState', ready: true, sessionID: 's1', status: 'idle',
  sessions: [{ id: 's1', title: '대화 하나' }], models: [{ id: 'provider/model', name: '모델 A' }], agents: [{ name: 'plan', description: '기획' }],
  messages: [
    { info: { id: 'u1', role: 'user' }, parts: [{ id: 'p1', type: 'text', text: '<img src=x onerror=alert(1)>' }] },
    { info: { id: 'a1', role: 'assistant' }, parts: [
      { id: 'p2', type: 'text', text: '안녕하세요' },
      { id: 'p3', type: 'reasoning', text: '검토 내용' },
      { id: 'p4', type: 'tool', tool: 'read', state: { status: 'completed', title: '파일 읽기', input: { path: 'x' }, output: '<script>bad</script>' } }
    ] }
  ],
  permissions: [{ id: 'perm-1', sessionID: 's1', permission: 'shell', patterns: ['git status'], metadata: { cwd: 'C:\\repo' } }],
  questions: [{ id: 'question-1', sessionID: 's1', questions: [
    { header: '방식', question: '어떤 방식인가요?', options: [{ label: 'A', description: '첫 선택' }, { label: 'B', description: '둘째 선택' }], multiple: false },
    { question: '추가 항목은?', options: [{ label: 'X' }, { label: 'Y' }], multiple: true, custom: true }
  ] }]
});
assert.equal(elements.messages.children.length, 2, 'user and assistant turns render');
assert.equal(elements.messages.children[0].children[1].children[0].textContent, '<img src=x onerror=alert(1)>', 'untrusted text remains literal text');
assert.equal(findAll(elements.messages, node => node.tagName === 'IMG' || node.tagName === 'SCRIPT').length, 0, 'message text cannot create active markup');
assert.equal(findAll(elements.messages, node => node.className === 'tool-value').length, 2, 'tool input and output are readable selectable text');
assert.equal(elements.modelWrap.hidden, false, 'host models reveal optional model picker');
assert.equal(elements.agentWrap.hidden, false, 'host agents reveal optional agent picker');

const stableMessage = elements.messages.children[1]; const stablePart = stableMessage.children[1].children[0];
postHost({ type: 'nativeState', ready: true, sessionID: 's1', status: 'idle',
  sessions: [{ id: 's1', title: '대화 하나' }], models: [{ id: 'provider/model', name: '모델 A' }], agents: [{ name: 'plan', description: '기획' }],
  messages: [
    { info: { id: 'u1', role: 'user' }, parts: [{ id: 'p1', type: 'text', text: '<img src=x onerror=alert(1)>' }] },
    { info: { id: 'a1', role: 'assistant' }, parts: [
      { id: 'p2', type: 'text', text: '안녕하세요' },
      { id: 'p3', type: 'reasoning', text: '검토 내용' },
      { id: 'p4', type: 'tool', tool: 'read', state: { status: 'completed', title: '파일 읽기', input: { path: 'x' }, output: '<script>bad</script>' } }
    ]
  }], permissions: [{ id: 'perm-1', sessionID: 's1', permission: 'shell', patterns: ['git status'], metadata: { cwd: 'C:\\repo' } }],
  questions: [{ id: 'question-1', sessionID: 's1', questions: [
    { header: '방식', question: '어떤 방식인가요?', options: [{ label: 'A', description: '첫 선택' }, { label: 'B', description: '둘째 선택' }], multiple: false },
    { question: '추가 항목은?', options: [{ label: 'X' }, { label: 'Y' }], multiple: true, custom: true }
  ] }]
});
assert.equal(elements.messages.children[1], stableMessage, 'duplicate snapshots retain the conversation node and selection anchor');
assert.equal(elements.messages.children[1].children[1].children[0], stablePart, 'unchanged text nodes are not rebuilt');

elements.composer.value = '초안 보존 확인';
const stablePermissionCard = elements.permissions.children[0]; const stableQuestionCard = elements.questions.children[0];
postHost({ type: 'nativeState', ready: false, error: '일시적인 상태 조회 오류' });
assert.equal(elements.messages.children[1], stableMessage, 'a partial error state retains existing conversation blocks');
assert.equal(elements.permissions.children[0], stablePermissionCard, 'a partial error state retains pending permission UI');
assert.equal(elements.questions.children[0], stableQuestionCard, 'a partial error state retains pending question UI');
assert.equal(elements.composer.value, '초안 보존 확인', 'a partial error state does not alter the composer draft');
assert.equal(elements['new-button'].disabled, true, 'partial reconnect state disables session mutations');
assert.equal(elements['session-select'].disabled, true, 'partial reconnect state disables history selection');
assert.equal(findAll(elements.permissions, node => node.tagName === 'BUTTON')[0].disabled, true, 'partial reconnect state disables permission replies');
postHost({ type: 'nativeState', ready: true, error: null });
assert.equal(elements.messages.children[1], stableMessage, 'a successful partial state keeps the conversation snapshot');
assert.equal(elements.notice.hidden, true, 'an explicit successful state clears the previous error');
assert.equal(elements.composer.value, '초안 보존 확인', 'reconnection keeps the unsent draft');
assert.equal(elements['new-button'].disabled, false, 'ready state restores session controls');

const permissionButtons = findAll(elements.permissions, node => node.tagName === 'BUTTON');
assert.equal(permissionButtons.length, 3, 'permission card exposes once, always and reject actions');
click(permissionButtons[0]); click(permissionButtons[1]); click(permissionButtons[2]);
assert.deepEqual(hostMessages.filter(message => message.type === 'nativePermission').map(message => message.reply), ['once','always','reject']);
const questionCard = elements.questions.children[0];
const inputs = findAll(questionCard, node => node.tagName === 'INPUT');
const questionButtons = findAll(questionCard, node => node.tagName === 'BUTTON');
assert.equal(questionButtons[1].disabled, true, 'question submit starts disabled until every question has an answer');
inputs[1].checked = true; inputs[1].dispatch('change'); inputs[2].checked = true; inputs[2].dispatch('change');
const custom = findAll(questionCard, node => node.tagName === 'TEXTAREA')[0]; custom.value = '직접 답';
click(questionButtons[0]); click(questionButtons[1]);
assert.deepEqual(hostMessages.filter(message => message.type === 'nativeQuestionReject').map(message => message.requestID), ['question-1']);
assert.deepEqual(JSON.parse(JSON.stringify(hostMessages.find(message => message.type === 'nativeQuestion').answers)), [['B'], ['X', '직접 답']], 'all question answers preserve per-question option and custom text');

elements.composer.value = '한국어 입력';
elements.composer.dispatch('input');
elements.model.value = 'provider/model'; elements.agent.value = 'plan';
let imeEvent = elements.composer.dispatch('keydown', { key: 'Enter', isComposing: true });
assert.equal(imeEvent.defaultPrevented, undefined, 'Enter during IME composition stays inside the composer');
imeEvent = elements.composer.dispatch('keydown', { key: 'Enter', keyCode: 229, isComposing: false });
assert.equal(imeEvent.defaultPrevented, undefined, 'legacy IME composition key code also avoids premature send');
const newlineEvent = elements.composer.dispatch('keydown', { key: 'Enter', shiftKey: true });
assert.equal(newlineEvent.defaultPrevented, undefined, 'Shift+Enter is left to insert a newline');
const submittedText = elements.composer.value;
const enterEvent = elements.composer.dispatch('keydown', { key: 'Enter' });
assert.equal(enterEvent.defaultPrevented, true, 'plain Enter submits');
assert.equal(sent().length, 1, 'plain Enter sends once');
assert.equal(sent()[0].text, submittedText, 'send preserves composed draft text');
assert.ok(sent()[0].requestID, 'each send has a unique request ID');
assert.equal(sent()[0].model, 'provider/model'); assert.equal(sent()[0].agent, 'plan');
assert.equal(elements.composer.value, submittedText, 'composer draft remains until host confirms the send');
assert.equal(elements['new-button'].disabled, true, 'session creation is disabled during an in-flight send');
assert.equal(elements['session-select'].disabled, true, 'history selection is disabled during an in-flight send');
postHost({ type: 'nativeSent', requestID: sent()[0].requestID, text: 'different input', sessionID: 's1' });
assert.equal(elements.composer.value, submittedText, 'mismatched acknowledgement cannot clear the draft');
elements.composer.value = '???? ?? ??'; elements.composer.dispatch('input');
postHost({ type: 'nativeSent', requestID: sent()[0].requestID, text: submittedText, sessionID: 's1' });
assert.equal(elements.composer.value, '???? ?? ??', 'acknowledgement cannot clear a subsequently edited draft');
assert.equal(elements['new-button'].disabled, false, 'acknowledgement releases the session controls');
elements.composer.value = '??? ??'; elements.composer.dispatch('input'); click(elements['send-button']);
const accepted = sent().at(-1);
postHost({ type: 'nativeSent', requestID: accepted.requestID, text: accepted.text, sessionID: 's1' });
assert.equal(elements.composer.value, '', 'matching accepted acknowledgement clears only the submitted draft');
assert.deepEqual(JSON.parse(JSON.stringify(hostMessages.at(-1))), { type: 'nativeDraft', text: '' }, 'accepted draft clearing is synchronized with host');

elements.composer.value = 'retry me'; elements.composer.dispatch('input'); click(elements['send-button']);
const rejected = sent().at(-1);
postHost({ type: 'nativeSendFailed', requestID: rejected.requestID, retryable: true, message: 'validation failed' });
assert.equal(elements.composer.value, 'retry me', 'known validation failures preserve the draft');
click(elements['send-button']);
const retried = sent().at(-1);
assert.notEqual(retried.requestID, rejected.requestID, 'retry gets a distinct request ID');
postHost({ type: 'nativeSent', requestID: rejected.requestID, text: 'retry me', sessionID: 's1' });
assert.equal(elements.composer.value, 'retry me', 'late acknowledgement from a rejected attempt is ignored');
postHost({ type: 'nativeSent', requestID: retried.requestID, text: 'retry me', sessionID: 's1' });
assert.equal(elements.composer.value, '', 'retry acknowledgement clears the unchanged draft');

elements.composer.value = 'receipt unknown'; elements.composer.dispatch('input'); click(elements['send-button']);
const uncertain = sent().at(-1);
postHost({ type: 'nativeSendFailed', requestID: uncertain.requestID, retryable: false, message: 'receipt unknown' });
assert.equal(elements.composer.value, 'receipt unknown', 'ambiguous failures preserve the draft');
assert.equal(elements['send-button'].disabled, true, 'ambiguous exact text cannot be blindly retried');
elements.composer.value = 'different message'; elements.composer.dispatch('input');
assert.equal(elements['send-button'].disabled, false, 'a different draft remains sendable after an ambiguous failure');
click(elements['send-button']);
const other = sent().at(-1);
postHost({ type: 'nativeSent', requestID: other.requestID, text: other.text, sessionID: 's1' });
assert.equal(elements.composer.value, '', 'another message can complete while an ambiguous draft stays guarded');

elements.composer.value = 'same text'; elements.composer.dispatch('input'); click(elements['send-button']);
const switched = sent().at(-1);
postHost({ type: 'nativeState', ready: true, sessionID: 's2' });
elements.composer.value = 'same text'; elements.composer.dispatch('input');
postHost({ type: 'nativeSent', requestID: switched.requestID, text: switched.text, sessionID: 's2' });
assert.equal(elements.composer.value, 'same text', 'late acknowledgement after a session switch and identical retype cannot clear the draft');

const sendsBeforeSetInput = sent().length;
postHost({ type: 'nativeSetInput', text: '붙여넣은 초안' });
assert.equal(elements.composer.value, '붙여넣은 초안', 'host input fills the composer without sending');
assert.equal(hostMessages.filter(message => message.type === 'nativeSend').length, sendsBeforeSetInput, 'nativeSetInput never submits');
elements.composer.value = 'alpha beta'; elements.composer.setSelectionRange(6, 10); elements.composer.focus();
const copyShortcut = { key: 'c', ctrlKey: true, shiftKey: true, preventDefault() { this.defaultPrevented = true; } };
for (const listener of documentListeners.keydown) listener(copyShortcut);
assert.equal(copyShortcut.defaultPrevented, true, 'Ctrl+Shift+C is bridged for composer selection');
assert.deepEqual(JSON.parse(JSON.stringify(hostMessages.at(-1))), { type: 'copy', data: 'beta' }, 'copy bridge returns selected textarea text');
postHost({ type: 'copySelection' });
assert.equal(hostMessages.at(-1).data, 'beta', 'host copy-selection command reads textarea selection');
postHost({ type: 'paste', data: 'XY' });
assert.equal(elements.composer.value, 'alpha XY', 'host clipboard text replaces the selected composer range');
assert.equal(hostMessages.at(-1).type, 'nativeDraft', 'programmatic paste reports the updated draft to host');
assert.equal(hostMessages.at(-1).text, 'alpha XY');
const pasteShortcut = { key: 'v', ctrlKey: true, preventDefault() { this.defaultPrevented = true; } };
for (const listener of documentListeners.keydown) listener(pasteShortcut);
assert.equal(pasteShortcut.defaultPrevented, true, 'Ctrl+V is routed through the host clipboard confirmation');
assert.equal(hostMessages.at(-1).type, 'paste', 'keyboard paste asks host for clipboard text');
const pasteEvent = { preventDefault() { this.prevented = true; }, stopImmediatePropagation() { this.stopped = true; } };
for (const listener of documentListeners.paste) listener(pasteEvent);
assert.equal(pasteEvent.prevented && pasteEvent.stopped, true, 'browser paste event cannot bypass host confirmation');
pageSelection = 'selected conversation';
const contextEvent = { preventDefault() { this.prevented = true; } };
for (const listener of documentListeners.contextmenu) listener(contextEvent);
assert.equal(contextEvent.prevented, true, 'default WebView context menu is replaced by native menu');
assert.deepEqual(JSON.parse(JSON.stringify(hostMessages.at(-1))), { type: 'contextMenu', hasSelection: true }, 'context menu reports selection availability');
postHost({ type: 'selectAll' });
assert.equal(pageSelection, elements.conversation.innerText, 'select-all targets conversation text');
postHost({ type: 'clearSelection' });
assert.equal(pageSelection, '', 'clear-selection removes browser text selection');
const displayedMessages = elements.messages.children.length;
postHost({ type: 'clear' });
assert.equal(elements.messages.children.length, displayedMessages, 'clear never deletes server-backed conversation data');
assert.ok(elements.notice.textContent.length > 0, 'clear gives an explicit no-delete notice');
document.activeElement = elements.conversation;
postHost({ type: 'focus' });
assert.equal(document.activeElement, elements.composer, 'explicit focus message focuses the composer when there is no selection');
pageSelection = 'keep this selection'; document.activeElement = elements.conversation;
postHost({ type: 'focus' });
assert.equal(document.activeElement, elements.conversation, 'explicit focus message does not collapse conversation selection');
assert.equal(pageSelection, 'keep this selection', 'focus keeps selected conversation text intact');
pageSelection = '';

const ctrlC = { key: 'c', ctrlKey: true, preventDefault() { this.defaultPrevented = true; } };
for (const listener of documentListeners.keydown) listener(ctrlC);
assert.equal(ctrlC.defaultPrevented, undefined, 'plain Ctrl+C keeps browser selection behavior');

elements['session-select'].value = 's1'; elements['session-select'].dispatch('change');
assert.equal(hostMessages.at(-1).type, 'nativeSelect', 'history selection is routed to the native host');
for (const key of ['t', 'm']) {
  const shortcut = { key, ctrlKey: true, shiftKey: true, preventDefault() { this.defaultPrevented = true; } };
  for (const listener of documentListeners.keydown) listener(shortcut);
  assert.equal(shortcut.defaultPrevented, true, `Ctrl+Shift+${key.toUpperCase()} is consumed`);
  assert.equal(JSON.stringify(hostMessages.at(-1)), JSON.stringify({ type: 'shortcut', key, shift: true }));
}
click(elements['new-button']); click(elements['refresh-button']);
assert.equal(hostMessages.at(-2).type, 'nativeNew'); assert.equal(hostMessages.at(-1).type, 'nativeRefresh');
postHost({ type: 'nativeState', ready: true, status: 'busy', messages: [], sessions: [], permissions: [], questions: [] });
assert.equal(elements['stop-button'].disabled, false, 'busy native state enables stop action');
assert.equal(elements['send-button'].disabled, true, 'busy native state disables send until ready');
click(elements['stop-button']); assert.equal(hostMessages.at(-1).type, 'nativeAbort');
postHost({ type: 'nativeNotice', level: 'error', message: '<b>실패</b>' });
assert.equal(elements.notice.textContent, '<b>실패</b>', 'host errors render as text');
postHost({ type: 'nativeNotice', level: 'error', message: '' });
postHost({ type: 'nativeState', ready: false, status: 'retry', error: '연결 오류' });
assert.equal(elements.notice.textContent, '연결 오류', 'native state errors remain visible');
assert.equal(elements['new-button'].disabled, true, 'reconnecting state disables new conversation');
postHost({ type: 'nativeState', ready: false, status: 'retry', error: null });
assert.equal(elements.notice.hidden, true, 'a later state without an error clears the previous state error');

console.log('PASS native conversation rendering, safe text and duplicate-snapshot selection stability');
console.log('PASS IME-safe Enter/Shift+Enter, exact-send acknowledgements and composer draft retention');
console.log('PASS permission/question decisions, history/session actions, optional model/agent and resize-independent layout');
