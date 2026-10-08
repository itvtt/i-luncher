(() => {
  'use strict';

  const byId = id => document.getElementById(id);
  const dom = {
    conversation: byId('conversation'), messages: byId('messages'), empty: byId('empty-state'),
    permissions: byId('permissions'), questions: byId('questions'), composer: byId('composer'),
    send: byId('send-button'), stop: byId('stop-button'), status: byId('status-label'),
    dot: byId('status-dot'), title: byId('session-title'), notice: byId('notice'),
    sessions: byId('session-select'), modelWrap: byId('model-wrap'), model: byId('model-select'),
    agentWrap: byId('agent-wrap'), agent: byId('agent-select')
  };
  const webview = window.chrome && window.chrome.webview;
  const post = message => { if (webview && typeof webview.postMessage === 'function') webview.postMessage(message); };
  const messageNodes = new Map();
  const messageSignatures = new Map();
  const promptNodes = { permissions: new Map(), questions: new Map() };
  const promptSignatures = { permissions: new Map(), questions: new Map() };
  const pendingSends = new Map();
  let nextSendId = 0;
  let activeSendId = null;
  let draftRevision = 0;
  let sessionGeneration = 0;
  let lastSessionId = null;
  let currentState = { ready: false, sessions: [], messages: [], permissions: [], questions: [], status: 'idle' };
  let noticeTimer = 0;
  let stateError = '';
  let transientNotice = null;

  const element = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  };
  const safeString = value => {
    if (typeof value === 'string') return value;
    if (value === null || value === undefined) return '';
    try { return JSON.stringify(value, null, 2); } catch { return String(value); }
  };
  const signature = value => {
    try { return JSON.stringify(value); } catch { return ''; }
  };
  const idOf = (value, fallback) => String((value && (value.id ?? value.requestID ?? value.info?.id)) ?? fallback);
  const clearMapExcept = (nodes, signatures, keep) => {
    for (const [id, node] of nodes) {
      if (!keep.has(id)) {
        node.remove();
        nodes.delete(id);
        signatures.delete(id);
      }
    }
  };
  const reconcile = (container, values, nodes, signatures, build, getId) => {
    const keep = new Set();
    values.forEach((value, index) => {
      const id = getId(value, index);
      keep.add(id);
      const nextSignature = signature(value);
      let node = nodes.get(id);
      if (!node) {
        node = build(value, id);
        nodes.set(id, node);
        signatures.set(id, nextSignature);
      } else if (signatures.get(id) !== nextSignature) {
        const replacement = build(value, id);
        node.replaceWith(replacement);
        node = replacement;
        nodes.set(id, node);
        signatures.set(id, nextSignature);
      }
      // Moving an unchanged node is only needed when the server changes order. Avoid touching
      // identical snapshots so the browser's native text selection remains intact.
      if (container.children[index] !== node) container.appendChild(node);
    });
    clearMapExcept(nodes, signatures, keep);
  };
  const valueArray = value => Array.isArray(value) ? value : [];
  const choiceId = item => String(item && typeof item === 'object' ? (item.id ?? item.name ?? item.value ?? item.label ?? '') : (item ?? ''));
  const choiceName = item => String(item && typeof item === 'object' ? (item.name ?? item.label ?? item.id ?? '') : (item ?? ''));
  const setChoices = (select, wrap, choices, placeholder) => {
    const list = valueArray(choices);
    const oldValue = select.value;
    const nextSignature = signature(list);
    if (select.dataset.signature === nextSignature) return;
    select.dataset.signature = nextSignature;
    select.replaceChildren();
    const first = element('option', '', placeholder);
    first.value = '';
    select.appendChild(first);
    for (const choice of list) {
      const option = element('option', '', choiceName(choice));
      option.value = choiceId(choice);
      select.appendChild(option);
    }
    const available = list.some(item => choiceId(item) === oldValue);
    select.value = available ? oldValue : '';
    wrap.hidden = list.length === 0;
  };
  const appendValue = (parent, label, value) => {
    if (value === undefined || value === null || value === '') return;
    parent.appendChild(element('div', 'tool-label', label));
    parent.appendChild(element('pre', 'tool-value', safeString(value)));
  };

  function renderTool(part) {
    const details = element('details', 'part tool');
    const summary = element('summary');
    summary.appendChild(element('span', '', safeString(part.tool || part.name || '도구')));
    const state = part.state || {};
    const status = safeString(state.status || '');
    if (status) summary.appendChild(element('span', 'tool-status', status));
    details.appendChild(summary);
    const content = element('div', 'tool-content');
    if (state.title) content.appendChild(element('div', 'tool-label', safeString(state.title)));
    appendValue(content, '입력', state.input);
    appendValue(content, '출력', state.output);
    if (state.error) {
      const error = element('div', 'message-error-block', safeString(state.error));
      content.appendChild(error);
    }
    if (content.childNodes.length) details.appendChild(content);
    return details;
  }

  function renderMessage(message) {
    const info = message && message.info || {};
    const role = String(info.role || 'assistant').toLowerCase();
    const styleRole = role === 'user' ? 'user' : (role === 'assistant' ? 'assistant' : 'other');
    const card = element('article', `message ${styleRole}`);
    const header = element('div', 'message-header');
    header.appendChild(element('span', 'role-dot'));
    header.appendChild(element('span', '', role === 'user' ? '나' : role === 'assistant' ? 'OpenCode' : role));
    if (info.error) header.appendChild(element('span', 'message-error', safeString(info.error))); 
    card.appendChild(header);
    const body = element('div', 'message-body');
    for (const part of valueArray(message && message.parts)) {
      if (!part || typeof part !== 'object') continue;
      if (part.type === 'text' || part.type === 'reasoning') {
        if (part.text !== undefined && part.text !== '') body.appendChild(element('div', `part ${part.type}`, safeString(part.text)));
      } else if (part.type === 'tool') {
        body.appendChild(renderTool(part));
      } else if (part.text !== undefined) {
        body.appendChild(element('div', 'part', safeString(part.text)));
      }
    }
    card.appendChild(body);
    return card;
  }

  function renderSessions(sessions, selectedId) {
    const oldValue = selectedId || dom.sessions.value;
    const sig = signature(sessions);
    if (dom.sessions.dataset.signature !== sig) {
      dom.sessions.dataset.signature = sig;
      dom.sessions.replaceChildren();
      dom.sessions.appendChild(element('option', '', '대화 기록')).value = '';
      for (const session of sessions) {
        const title = session.title || session.id || '새 대화';
        const timestamp = typeof session.time === 'number' ? session.time : session.time?.updated ?? session.time?.created;
        const date = Number.isFinite(timestamp) ? new Date(timestamp) : null;
        const time = date && Number.isFinite(date.getTime())
          ? ` · ${date.toLocaleString('ko-KR', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })}` : '';
        const option = element('option', '', `${title}${time}`);
        option.value = String(session.id || '');
        dom.sessions.appendChild(option);
      }
    }
    if (oldValue !== undefined && oldValue !== null) dom.sessions.value = String(oldValue);
    const active = sessions.find(session => String(session.id) === String(selectedId));
    dom.title.textContent = active ? String(active.title || '') : '';
  }

  function renderPermission(item, id) {
    const card = element('section', 'pending-card');
    card.appendChild(element('h2', 'pending-title', '권한 승인 요청'));
    card.appendChild(element('div', 'pending-detail', item.permission || 'OpenCode가 작업 권한을 요청했습니다.'));
    if (Array.isArray(item.patterns) && item.patterns.length) card.appendChild(element('div', 'permission-patterns', item.patterns.map(safeString).join('\n')));
    if (item.metadata && Object.keys(item.metadata).length) card.appendChild(element('div', 'permission-meta', safeString(item.metadata)));
    const actions = element('div', 'pending-actions');
    for (const [label, reply, cls] of [['이번만 허용', 'once', 'approve'], ['항상 허용', 'always', 'approve'], ['거부', 'reject', 'reject']]) {
      const button = element('button', `small-button ${cls}`, label);
      button.type = 'button';
      button.disabled = !currentState.ready;
      button.addEventListener('click', () => post({ type: 'nativePermission', requestID: id, reply }));
      actions.appendChild(button);
    }
    card.appendChild(actions);
    return card;
  }

  function renderQuestion(item, id) {
    const card = element('section', 'pending-card question-card');
    card.appendChild(element('h2', 'pending-title', '질문에 답해주세요'));
    const answers = [];
    for (const [index, question] of valueArray(item.questions).entries()) {
      const group = element('div', 'question-item');
      if (question.header) group.appendChild(element('div', 'question-header', question.header));
      group.appendChild(element('div', 'question-prompt', question.question || ''));
    const inputs = [];
      for (const [optionIndex, option] of valueArray(question.options).entries()) {
        const label = element('label', 'answer-option');
        const input = element('input');
        input.type = question.multiple ? 'checkbox' : 'radio';
        input.name = `q-${id}-${index}`;
        input.dataset.answerOption = String(optionIndex);
        input.value = String(option.label ?? '');
        inputs.push(input);
        label.appendChild(input);
        const copy = element('span', 'option-copy');
        copy.appendChild(element('span', 'option-label', option.label || ''));
        if (option.description) copy.appendChild(element('div', 'option-description', option.description));
        label.appendChild(copy);
        group.appendChild(label);
      }
      let customInput = null;
      if (question.custom) {
        customInput = element('textarea', 'custom-answer');
        customInput.rows = 1;
        customInput.placeholder = '직접 답변…';
        customInput.setAttribute('aria-label', `질문 ${index + 1} 직접 답변`);
        group.appendChild(customInput);
      }
      answers[index] = () => {
        const selected = inputs.filter(input => input.checked).map(input => input.value);
        if (customInput && customInput.value.trim()) selected.push(customInput.value.trim());
        return selected;
      };
      const refreshAnswerState = () => {
        submit.disabled = !currentState.ready || answers.length === 0 || !answers.every(read => read().length > 0);
      };
      for (const input of inputs) input.addEventListener('change', refreshAnswerState);
      if (customInput) customInput.addEventListener('input', refreshAnswerState);
      card.appendChild(group);
    }
    const actions = element('div', 'pending-actions');
    const reject = element('button', 'small-button reject', '질문 거부');
    reject.type = 'button';
    reject.disabled = !currentState.ready;
    reject.addEventListener('click', () => post({ type: 'nativeQuestionReject', requestID: id }));
    const submit = element('button', 'small-button approve', '답변 보내기');
    submit.type = 'button';
    submit.refreshAnswerState = () => {
      submit.disabled = !currentState.ready || answers.length === 0 || !answers.every(read => read().length > 0);
    };
    submit.disabled = !currentState.ready || answers.length === 0 || !answers.every(read => read().length > 0);
    submit.addEventListener('click', () => {
      const resolved = answers.map(read => read());
      if (resolved.length && resolved.every(answer => answer.length > 0))
        post({ type: 'nativeQuestion', requestID: id, answers: resolved });
    });
    actions.appendChild(reject);
    actions.appendChild(submit);
    card.appendChild(actions);
    return card;
  }

  function updateSendEnabled() {
    const text = dom.composer.value;
    const sameTextPending = Array.from(pendingSends.values()).some(send => send.text === text);
    dom.send.disabled = !currentState.ready || currentState.status === 'busy' || activeSendId !== null || !text.trim() || sameTextPending;
    byId('new-button').disabled = !currentState.ready || currentState.status === 'busy' || activeSendId !== null;
    dom.sessions.disabled = !currentState.ready || currentState.status === 'busy' || activeSendId !== null;
  }

  function sendDraft() {
    const text = dom.composer.value;
    if (!currentState.ready || currentState.status === 'busy' || activeSendId !== null || !text.trim() ||
        Array.from(pendingSends.values()).some(send => send.text === text)) return;
    const requestID = `native-${Date.now().toString(36)}-${(++nextSendId).toString(36)}`;
    const payload = { type: 'nativeSend', requestID, text };
    if (dom.model.value) payload.model = dom.model.value;
    if (dom.agent.value) payload.agent = dom.agent.value;
    pendingSends.set(requestID, { text, draftRevision, sessionID: currentState.sessionID || '', sessionGeneration, ambiguous: false });
    activeSendId = requestID;
    updateSendEnabled();
    post(payload);
  }

  function renderState(state) {
    if (Object.prototype.hasOwnProperty.call(state, 'sessionID')) {
      const nextSessionId = state.sessionID || null;
      if (nextSessionId !== lastSessionId) {
        sessionGeneration++;
        lastSessionId = nextSessionId;
      }
    }
    currentState = { ...currentState, ...state };
    if (Object.prototype.hasOwnProperty.call(state, 'error'))
      stateError = state.error ? safeString(state.error) : '';
    const sessions = valueArray(currentState.sessions);
    renderSessions(sessions, currentState.sessionID || '');
    setChoices(dom.model, dom.modelWrap, currentState.models, '기본 모델');
    setChoices(dom.agent, dom.agentWrap, currentState.agents, '기본 에이전트');

    const messages = valueArray(currentState.messages);
    reconcile(dom.messages, messages, messageNodes, messageSignatures, renderMessage,
      (message, index) => idOf(message, `message-${index}`));
    dom.empty.hidden = messages.length > 0;
    reconcile(dom.permissions, valueArray(currentState.permissions), promptNodes.permissions,
      promptSignatures.permissions, renderPermission, idOf);
    reconcile(dom.questions, valueArray(currentState.questions), promptNodes.questions,
      promptSignatures.questions, renderQuestion, idOf);

    const status = currentState.status || 'idle';
    dom.dot.className = `status-dot${status === 'busy' ? ' busy' : status === 'retry' ? ' retry' : ''}`;
    dom.status.textContent = status === 'busy' ? '진행 중' : status === 'retry' ? '재연결 중' : currentState.ready ? '대기' : '연결 중';
    dom.stop.disabled = !currentState.ready || status !== 'busy';
    for (const button of dom.permissions.querySelectorAll('button')) button.disabled = !currentState.ready;
    for (const button of dom.questions.querySelectorAll('button')) {
      if (typeof button.refreshAnswerState === 'function') button.refreshAnswerState();
      else button.disabled = !currentState.ready;
    }
    updateSendEnabled();
    renderNotice();
  }

  function renderNotice() {
    const message = stateError || (transientNotice && transientNotice.message) || '';
    const level = stateError ? 'error' : transientNotice ? transientNotice.level : 'error';
    dom.notice.textContent = safeString(message);
    dom.notice.className = `notice${level === 'info' ? ' info' : ''}`;
    dom.notice.hidden = !message;
  }
  function showNotice(message, level = 'error') {
    if (noticeTimer) clearTimeout(noticeTimer);
    transientNotice = message ? { message: safeString(message), level } : null;
    if (message) noticeTimer = setTimeout(() => { transientNotice = null; renderNotice(); }, level === 'error' ? 9000 : 4500);
    renderNotice();
  }

  function selectedText() {
    if (document.activeElement === dom.composer && Number.isInteger(dom.composer.selectionStart) &&
        Number.isInteger(dom.composer.selectionEnd) && dom.composer.selectionStart !== dom.composer.selectionEnd)
      return dom.composer.value.slice(dom.composer.selectionStart, dom.composer.selectionEnd);
    const selection = window.getSelection && window.getSelection();
    return selection ? String(selection) : '';
  }

  function insertPaste(text) {
    const value = String(text ?? '');
    const start = Number.isInteger(dom.composer.selectionStart) ? dom.composer.selectionStart : dom.composer.value.length;
    const end = Number.isInteger(dom.composer.selectionEnd) ? dom.composer.selectionEnd : start;
    dom.composer.value = dom.composer.value.slice(0, start) + value + dom.composer.value.slice(end);
    const caret = start + value.length;
    if (typeof dom.composer.setSelectionRange === 'function') dom.composer.setSelectionRange(caret, caret);
    dom.composer.focus();
    draftRevision++;
    updateSendEnabled();
    post({ type: 'nativeDraft', text: dom.composer.value });
  }

  function selectConversation() {
    if (!window.getSelection || !document.createRange) return;
    const selection = window.getSelection();
    const range = document.createRange();
    range.selectNodeContents(dom.conversation);
    selection.removeAllRanges();
    selection.addRange(range);
  }

  function clearSelection() {
    const selection = window.getSelection && window.getSelection();
    if (selection) selection.removeAllRanges();
    if (document.activeElement === dom.composer && Number.isInteger(dom.composer.selectionEnd) &&
        typeof dom.composer.setSelectionRange === 'function')
      dom.composer.setSelectionRange(dom.composer.selectionEnd, dom.composer.selectionEnd);
  }

  if (webview && typeof webview.addEventListener === 'function') {
    webview.addEventListener('message', event => {
      const message = event && event.data || {};
      if (message.type === 'nativeState') renderState(message);
      else if (message.type === 'nativeNotice') showNotice(message.message, message.level);
      else if (message.type === 'nativeSent') {
        const requestID = String(message.requestID ?? '');
        const pending = pendingSends.get(requestID);
        const ackText = String(message.text ?? '');
        if (!pending || pending.text !== ackText) return;
        pendingSends.delete(requestID);
        if (activeSendId === requestID) activeSendId = null;
        const ackSessionMatches = !message.sessionID || !pending.sessionID || String(message.sessionID) === pending.sessionID;
        if (dom.composer.value === ackText && draftRevision === pending.draftRevision &&
            sessionGeneration === pending.sessionGeneration && ackSessionMatches) {
          dom.composer.value = '';
          draftRevision++;
          post({ type: 'nativeDraft', text: '' });
        }
        updateSendEnabled();
      } else if (message.type === 'nativeSendFailed') {
        const requestID = String(message.requestID ?? '');
        const pending = pendingSends.get(requestID);
        if (!pending) return;
        if (activeSendId === requestID) activeSendId = null;
        if (message.retryable === true) {
          pendingSends.delete(requestID);
          showNotice(message.message || '전송 전에 요청이 거부되었습니다. 초안을 다시 보낼 수 있습니다.', 'error');
        } else {
          pending.ambiguous = true;
          showNotice(message.message || '전송 확인을 받지 못했습니다. 중복 전송을 막기 위해 같은 내용은 보류합니다.', 'error');
        }
        updateSendEnabled();
      } else if (message.type === 'nativeSetInput' && typeof message.text === 'string') {
        dom.composer.value = message.text;
        draftRevision++;
        dom.composer.focus();
        updateSendEnabled();
        post({ type: 'nativeDraft', text: dom.composer.value });
      } else if (message.type === 'copySelection') {
        post({ type: 'copy', data: selectedText() });
      } else if (message.type === 'selectAll') {
        selectConversation();
      } else if (message.type === 'clearSelection') {
        clearSelection();
      } else if (message.type === 'paste' && typeof message.data === 'string') {
        insertPaste(message.data);
      } else if (message.type === 'paste') {
        post({ type: 'paste' });
      } else if (message.type === 'clear') {
        showNotice('대화 기록은 서버에 저장됩니다. 이 화면에서는 메시지를 삭제하지 않습니다.', 'info');
      } else if (message.type === 'focus') {
        if (!selectedText()) dom.composer.focus();
      }
    });
  }

  dom.composer.addEventListener('input', () => {
    draftRevision++;
    updateSendEnabled();
    post({ type: 'nativeDraft', text: dom.composer.value });
  });
  document.addEventListener('paste', event => {
    event.preventDefault();
    event.stopImmediatePropagation();
    post({ type: 'paste' });
  }, true);
  document.addEventListener('contextmenu', event => {
    event.preventDefault();
    post({ type: 'contextMenu', hasSelection: selectedText().length > 0 });
  });
  dom.composer.addEventListener('keydown', event => {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing && event.keyCode !== 229) {
      event.preventDefault();
      sendDraft();
    }
  });
  dom.send.addEventListener('click', sendDraft);
  dom.stop.addEventListener('click', () => post({ type: 'nativeAbort' }));
  byId('new-button').addEventListener('click', () => post({ type: 'nativeNew' }));
  byId('refresh-button').addEventListener('click', () => post({ type: 'nativeRefresh' }));
  dom.sessions.addEventListener('change', () => {
    if (activeSendId === null && dom.sessions.value) post({ type: 'nativeSelect', sessionID: dom.sessions.value });
  });
  document.addEventListener('keydown', event => {
    const key = String(event.key || '').toLowerCase();
    if ((event.ctrlKey && event.shiftKey && key === 'c') || (event.ctrlKey && key === 'v') ||
        (event.shiftKey && key === 'insert')) {
      event.preventDefault();
      if (key === 'c') post({ type: 'copy', data: selectedText() });
      else post({ type: 'paste' });
      return;
    }
    if (event.ctrlKey && event.shiftKey && !event.altKey && !event.isComposing) {
      if (key === 't' || key === 'm') {
        event.preventDefault();
        post({ type: 'shortcut', key, shift: true });
      }
    }
  });
  post({ type: 'nativeReady' });
})();
