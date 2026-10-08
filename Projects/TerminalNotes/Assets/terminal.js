'use strict';
const defaultAnsiTheme={black:'#17202c',red:'#f18188',green:'#82d8a2',yellow:'#edcf86',blue:'#7aaff1',magenta:'#b5a0ee',cyan:'#75ced1',white:'#d4dfeb',brightBlack:'#63758b',brightRed:'#ff9ba0',brightGreen:'#a5edbd',brightYellow:'#ffe2a1',brightBlue:'#9ac5ff',brightMagenta:'#d0baff',brightCyan:'#98ecef',brightWhite:'#ffffff'};
// An orange confirmation surface needs dark ANSI colors; retaining the hue keeps logs distinguishable.
const orangeAnsiTheme={black:'#211000',red:'#7b1027',green:'#084f25',yellow:'#674000',blue:'#073e8a',magenta:'#602477',cyan:'#00585c',white:'#3c2700',brightBlack:'#493000',brightRed:'#930025',brightGreen:'#00602f',brightYellow:'#513400',brightBlue:'#00449d',brightMagenta:'#6b1885',brightCyan:'#006268',brightWhite:'#211000'};
let pendingStateBackgroundSgr='';
const maximumPendingStateBackgroundSgr=1024;
const withoutSgrBackground = data => {
  let input=pendingStateBackgroundSgr+data;
  pendingStateBackgroundSgr='';
  const trailingCsi=input.lastIndexOf('\x1b[');
  if(trailingCsi>=0 && !/[\x40-\x7e]/.test(input.slice(trailingCsi+2))){
    const pending=input.slice(trailingCsi);
    if(pending.length<=maximumPendingStateBackgroundSgr){
      pendingStateBackgroundSgr=pending;
      input=input.slice(0,trailingCsi);
    }else{
      // Abort malformed, unbounded CSI input instead of retaining it between writes.
      input+='\x1b[0m';
    }
  }else if(input.endsWith('\x1b')){
    pendingStateBackgroundSgr='\x1b';
    input=input.slice(0,-1);
  }
  return input.replace(/\x1b\[([0-9;:]*)m/g, (sequence, parameters) => {
    const codes=parameters.split(';');
    const kept=[];
    for(let index=0;index<codes.length;index++){
      const code=codes[index].split(':',1)[0];
      if(code==='38'||code==='58'){
        if(codes[index].includes(':')){
          kept.push(codes[index]);
          continue;
        }
        const mode=codes[index+1];
        const colorParameters=mode==='5' ? 2 : mode==='2' ? 4 : 0;
        for(let offset=0;offset<=colorParameters && index+offset<codes.length;offset++)
          kept.push(codes[index+offset]);
        index+=colorParameters;
        continue;
      }
      if(/^(?:4[0-7]|10[0-7])$/.test(code)) continue;
      if(code==='48'){
        if(codes[index].includes(':')) continue;
        if(codes[index+1]==='5') index+=2;
        else if(codes[index+1]==='2') index+=4;
        else index++;
        continue;
      }
      kept.push(codes[index]);
    }
    return kept.length ? `\x1b[${kept.join(';')}m` : '';
  });
};
const terminal = new Terminal({cursorBlink:true, fontSize:15, lineHeight:1.12, fontFamily:'"Cascadia Mono", Consolas, "Malgun Gothic", monospace', scrollback:10000, allowProposedApi:false, minimumContrastRatio:7, theme:{...defaultAnsiTheme,background:'#0c1016',foreground:'#edf3fb',cursor:'#75e0bd',selectionBackground:'#304856',selectionForeground:'#ffffff'}});
const fit = new FitAddon.FitAddon();
terminal.loadAddon(fit);
terminal.open(document.getElementById('terminal'));
const protectTuiViewport = new URLSearchParams(location.search).get('tui') === 'true';
let forceStateBackground=protectTuiViewport;
terminal.element.dataset.forceStateBackground=String(forceStateBackground);
const viewport = document.getElementById('viewport');
const terminalHost = document.getElementById('terminal');
if(protectTuiViewport) viewport.classList.add('tui');
// Keep one scrollbar at the visible edge, even when the protected 40x24 TUI
// is wider/taller than its pane. The xterm scrollbar otherwise sits off-screen.
const verticalScrollbar=document.getElementById('terminal-scrollbar');
let terminalCompositionActive=false;
let scrollbarPointerFocus=null;
terminal.textarea.addEventListener('compositionstart',()=>{terminalCompositionActive=true;});
terminal.textarea.addEventListener('compositionend',()=>{terminalCompositionActive=false;});
verticalScrollbar.addEventListener('pointerdown',event=>{
  scrollbarPointerFocus={
    pointerId:event.pointerId,
    restore:!terminalCompositionActive && document.activeElement===terminal.textarea
  };
},true);
const finishScrollbarPointer=event=>{
  const gesture=scrollbarPointerFocus;
  if(!gesture || gesture.pointerId!==event.pointerId) return;
  scrollbarPointerFocus=null;
  if(!gesture.restore) return;
  setTimeout(()=>{
    // A pointer click may take focus from xterm. Return it only when this pane
    // owned focus before the gesture; keyboard users can still tab to the rail.
    if(!terminalCompositionActive && document.activeElement===verticalScrollbar) terminal.focus();
  },0);
};
window.addEventListener('pointerup',finishScrollbarPointer,true);
window.addEventListener('pointercancel',finishScrollbarPointer,true);
const scrollSpacer=document.getElementById('scroll-spacer');
let applyingScrollbar=false;
let lastViewportPosition=-1;
const scrollMetrics=()=>{
  const screen=terminal.element.querySelector('.xterm-screen');
  const cellHeight=screen && screen.clientHeight>0 ? screen.clientHeight/terminal.rows : 17;
  const history=terminal.buffer.active.type==='normal' ? terminal.buffer.active.baseY : 0;
  const clipped=Math.max(0,viewport.scrollHeight-viewport.clientHeight);
  return {cellHeight,history,clipped,max:history*cellHeight+clipped};
};
const syncVerticalScrollbar=()=>{
  const m=scrollMetrics();
  scrollSpacer.style.height=(verticalScrollbar.clientHeight+m.max)+'px';
  const position=terminal.buffer.active.viewportY*m.cellHeight+viewport.scrollTop;
  if(applyingScrollbar) return;
  // Do not round away small touchpad deltas whenever new output is rendered.
  if(position!==lastViewportPosition && Math.abs(verticalScrollbar.scrollTop-position)>1)
    verticalScrollbar.scrollTop=position;
  lastViewportPosition=position;
};
const scrollVertically=position=>{
  const m=scrollMetrics();
  const top=Math.max(0,Math.min(m.max,position));
  const line=Math.min(m.history,Math.floor(top/m.cellHeight));
  applyingScrollbar=true;
  try {
    if(terminal.buffer.active.viewportY!==line) terminal.scrollToLine(line);
    viewport.scrollTop=Math.max(0,top-m.history*m.cellHeight);
    lastViewportPosition=terminal.buffer.active.viewportY*m.cellHeight+viewport.scrollTop;
  } finally { applyingScrollbar=false; }
};
verticalScrollbar.addEventListener('scroll',()=>scrollVertically(verticalScrollbar.scrollTop));
verticalScrollbar.addEventListener('wheel',event=>{
  // The rail sits just outside the clipped xterm viewport. Forward its wheel
  // to the visible terminal edge so mouse-reporting TUIs can scroll their own
  // content even when the alternate buffer has no xterm scrollback.
  if(terminal.modes.mouseTrackingMode==='none') return;
  const rect=terminal.element.getBoundingClientRect();
  const clip=viewport.getBoundingClientRect();
  const clientX=Math.max(rect.left+1,Math.min(rect.right-1,clip.right-1));
  const clientY=Math.max(rect.top+1,Math.min(rect.bottom-1,event.clientY));
  event.preventDefault();
  event.stopImmediatePropagation();
  terminal.element.dispatchEvent(new WheelEvent('wheel',{
    deltaX:event.deltaX,deltaY:event.deltaY,deltaMode:event.deltaMode,
    clientX,clientY,altKey:event.altKey,ctrlKey:event.ctrlKey,metaKey:event.metaKey,
    shiftKey:event.shiftKey,bubbles:true,cancelable:true
  }));
},{capture:true,passive:false});
viewport.addEventListener('scroll',syncVerticalScrollbar);
terminal.onScroll(syncVerticalScrollbar);
terminal.onRender(syncVerticalScrollbar);
terminal.buffer.onBufferChange(()=>{viewport.scrollTop=0;syncVerticalScrollbar();});
viewport.addEventListener('wheel',event=>{
  const horizontal=event.shiftKey || Math.abs(event.deltaX)>Math.abs(event.deltaY);
  // Mouse-reporting TUIs own the ordinary wheel. Swallowing it here prevents
  // OpenCode from scrolling its conversation, especially in split panes.
  if(!horizontal && terminal.modes.mouseTrackingMode!=='none') return;
  const m=scrollMetrics();
  const delta=(horizontal ? (event.deltaX || event.deltaY) : event.deltaY);
  const limit=horizontal ? viewport.scrollWidth-viewport.clientWidth : m.max;
  if(delta===0 || limit<=0) return;
  const unit=event.deltaMode===1 ? m.cellHeight : event.deltaMode===2 ? (horizontal ? viewport.clientWidth : viewport.clientHeight) : 1;
  event.preventDefault();
  event.stopImmediatePropagation();
  if(horizontal) viewport.scrollLeft+=delta*unit;
  else {
    verticalScrollbar.scrollTop+=delta*unit;
    scrollVertically(verticalScrollbar.scrollTop);
  }
},{capture:true,passive:false});
const fitTerminal = () => {
  if(document.body.clientWidth<=0 || document.body.clientHeight<=0) return;
  if(protectTuiViewport){
    const screen=terminal.element.querySelector('.xterm-screen');
    if(!screen || screen.clientWidth<=0 || screen.clientHeight<=0) return;
    terminalHost.style.minWidth=Math.ceil(screen.clientWidth/terminal.cols*40+16)+'px';
    terminalHost.style.minHeight=Math.ceil(screen.clientHeight/terminal.rows*24)+'px';
    const dimensions=fit.proposeDimensions();
    if(dimensions) terminal.resize(Math.max(40,dimensions.cols),Math.max(24,dimensions.rows));
  }else fit.fit();
  syncVerticalScrollbar();
};
const send = value => window.chrome.webview.postMessage(value);
let lastUserActivation = 0;
const activatedByUser = () => {
  const now = performance.now();
  if(now-lastUserActivation<100) return;
  lastUserActivation=now;
  send({type:'activated'});
};
terminal.element.addEventListener('pointerdown',activatedByUser);
let terminalMouseButtons=0;
let terminalMouseGesture=0;
let lastTerminalMouseEvent={button:0,clientX:0,clientY:0,altKey:false,ctrlKey:false,metaKey:false,shiftKey:false};
const rememberTerminalMouseEvent=(event,updateButton=false)=>{
  lastTerminalMouseEvent={button:updateButton&&event.button>=0?event.button:lastTerminalMouseEvent.button,clientX:event.clientX,clientY:event.clientY,altKey:event.altKey,ctrlKey:event.ctrlKey,metaKey:event.metaKey,shiftKey:event.shiftKey};
};
const releaseLostTerminalMouse=(expectedGesture=terminalMouseGesture)=>{
  if(!terminalMouseButtons||expectedGesture!==terminalMouseGesture) return;
  terminalMouseButtons=0;
  const event=lastTerminalMouseEvent;
  terminal.element.dispatchEvent(new MouseEvent('mouseup',{view:window,bubbles:true,cancelable:true,button:event.button,buttons:0,clientX:event.clientX,clientY:event.clientY,altKey:event.altKey,ctrlKey:event.ctrlKey,metaKey:event.metaKey,shiftKey:event.shiftKey}));
};
terminal.element.addEventListener('mousedown',event=>{
  if(event.button<0) return;
  terminalMouseGesture++;
  terminalMouseButtons=event.buttons || (1<<event.button);
  rememberTerminalMouseEvent(event,true);
},true);
window.addEventListener('mouseup',event=>{
  if(!terminalMouseButtons) return;
  rememberTerminalMouseEvent(event,true);
  terminalMouseButtons=event.buttons;
},true);
window.addEventListener('pointerup',event=>{
  if(event.pointerType!=='mouse'||!terminalMouseButtons||event.buttons!==0) return;
  rememberTerminalMouseEvent(event,true);
  const gesture=terminalMouseGesture;
  setTimeout(()=>releaseLostTerminalMouse(gesture),0);
},true);
window.addEventListener('pointermove',event=>{
  if(event.pointerType!=='mouse'||!terminalMouseButtons) return;
  rememberTerminalMouseEvent(event);
  if(event.buttons===0) releaseLostTerminalMouse();
},true);
const releaseCancelledTerminalPointer=event=>{
  if(event.pointerType==='mouse') releaseLostTerminalMouse();
};
window.addEventListener('pointercancel',releaseCancelledTerminalPointer,true);
window.addEventListener('mousemove',event=>{
  if(!terminalMouseButtons) return;
  rememberTerminalMouseEvent(event);
  if(event.buttons===0) releaseLostTerminalMouse();
},true);
terminal.element.addEventListener('lostpointercapture',releaseCancelledTerminalPointer,true);
window.addEventListener('blur',()=>releaseLostTerminalMouse());
document.addEventListener('visibilitychange',()=>{if(document.hidden) releaseLostTerminalMouse();});
// Native focus may change during WebView reparenting; only user interaction
// selects the command target, so delayed focus events cannot select an old tab.
terminal.element.addEventListener('keydown',activatedByUser);
const renderedTail = () => {
  const buffer=terminal.buffer.active;
  const end=buffer.baseY+buffer.cursorY;
  const start=Math.max(0,end-11);
  const lines=[];
  for(let row=start;row<=end;row++){
    const line=buffer.getLine(row);
    if(line){
      const text=line.translateToString(true);
      if(line.isWrapped && lines.length) lines[lines.length-1]+=text;
      else lines.push(text);
    }
  }
  return lines.join('\n');
};
let activityReportTimer=0;
const renderedScreen=()=>{
  const buffer=terminal.buffer.active;
  const lines=[];
  for(let row=buffer.baseY;row<buffer.baseY+terminal.rows;row++){
    const line=buffer.getLine(row);
    if(!line) continue;
    const text=line.translateToString(true);
    if(line.isWrapped && lines.length) lines[lines.length-1]+=text;
    else lines.push(text);
  }
  return lines.join('\n').trimEnd();
};
const reportActivity=()=>{
  if(activityReportTimer) return;
  activityReportTimer=setTimeout(()=>{
    activityReportTimer=0;
    const buffer=terminal.buffer.active;
    const line=buffer.getLine(buffer.baseY+buffer.cursorY);
    const prefix=line ? line.translateToString(false,0,buffer.cursorX) : '';
    const promptAtCursor=/^\s*[›❯>]\s*$/.test(prefix);
    send({type:'activityTail',data:renderedTail(),screen:renderedScreen(),layout:terminal.cols+'x'+terminal.rows,promptAtCursor});
  },200);
};
// Route Shift+Insert and native paste through the same host confirmation as Ctrl+V.
document.addEventListener('paste',e=>{e.preventDefault();e.stopImmediatePropagation();send({type:'paste'});},true);
document.addEventListener('contextmenu',e=>{e.preventDefault();send({type:'contextMenu',hasSelection:terminal.hasSelection()});});
let programmaticInput=false;
let programmaticSendId=null;
terminal.onData(data => send({type:'input',data,programmatic:programmaticInput,...(programmaticSendId===null?{}:{sendId:programmaticSendId})}));
terminal.onResize(({cols,rows}) => {send({type:'resize',cols,rows});reportActivity();});
terminal.attachCustomKeyEventHandler(e => {
  if(e.type !== 'keydown') return true;
  if(e.altKey) return true;
  const key=e.key.toLowerCase();
  if(e.shiftKey && !e.ctrlKey && ['pageup','pagedown','home','end'].includes(key) && scrollMetrics().max>0){
    e.preventDefault();
    const m=scrollMetrics();
    const top=key==='home' ? 0 : key==='end' ? m.max : verticalScrollbar.scrollTop+(key==='pageup' ? -1 : 1)*viewport.clientHeight;
    verticalScrollbar.scrollTop=top;
    scrollVertically(verticalScrollbar.scrollTop);
    return false;
  }
  if(e.ctrlKey && e.shiftKey && key==='c'){e.preventDefault();send({type:'copy',data:terminal.getSelection()});return false;}
  if(e.ctrlKey && key==='v'){e.preventDefault();send({type:'paste'});return false;}
  if(e.ctrlKey && e.shiftKey && ['t','m'].includes(key)){e.preventDefault();if(!e.repeat)send({type:'shortcut',key,shift:true});return false;}
  return true;
});
window.chrome.webview.addEventListener('message', e => {
  const m=e.data;
  if(m.type==='output') terminal.write(forceStateBackground ? withoutSgrBackground(m.data) : m.data,()=>{send({type:'drained'});reportActivity();});
  else if(m.type==='focus'){fitTerminal();terminal.focus();}
  else if(m.type==='theme'){
    const background=m.background;
    const foreground=m.foreground;
    if(typeof background!=='string'||typeof foreground!=='string') return;
    if(typeof m.forceStateBackground==='boolean') forceStateBackground=m.forceStateBackground;
    terminal.element.dataset.forceStateBackground=String(forceStateBackground);
    const ansi=m.useDarkAnsi===true ? orangeAnsiTheme : defaultAnsiTheme;
    terminal.options.theme={...terminal.options.theme,...ansi,background,foreground,
      cursor:typeof m.cursor==='string'?m.cursor:foreground,
      selectionBackground:typeof m.selectionBackground==='string'?m.selectionBackground:'#43536b',
      selectionForeground:typeof m.selectionForeground==='string'?m.selectionForeground:foreground};
    document.documentElement.style.setProperty('--terminal-background',background);
    document.documentElement.style.backgroundColor=background;
    document.body.style.backgroundColor=background;
    document.getElementById('terminal').style.backgroundColor=background;
  }
  else if(m.type==='paste') terminal.paste(m.data);
  else if(m.type==='copySelection') send({type:'copy',data:terminal.getSelection()});
  else if(m.type==='selectAll') terminal.selectAll();
  else if(m.type==='clearSelection') terminal.clearSelection();
  else if(m.type==='sendText'){
    try{
      programmaticInput=true;
      programmaticSendId=m.id;
      terminal.paste(typeof m.data==='string' ? m.data : '');
      if(m.submit) send({type:'input',data:'\r',programmatic:true,sendId:m.id});
      send({type:'sendTextAck',id:m.id});
    }catch(error){
      send({type:'sendTextAck',id:m.id,error:String(error)});
    }finally{
      programmaticInput=false;
      programmaticSendId=null;
    }
  }
  else if(m.type==='clear') terminal.clear();
});
new ResizeObserver(fitTerminal).observe(viewport);
fitTerminal();send({type:'ready',cols:terminal.cols,rows:terminal.rows});
