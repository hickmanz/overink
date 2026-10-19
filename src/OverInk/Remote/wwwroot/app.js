const params = new URLSearchParams(location.search);
const allowMouse = params.get('mouse') === '1'
  || location.hostname === 'localhost'
  || location.hostname === '127.0.0.1';
// Finger drawing is opt-in (?touch=1). Default: Pencil/mouse draw, fingers pan/zoom.
const allowTouchDraw = params.get('touch') === '1';
const deviceName = localStorage.getItem('overink.deviceName') || 'iPad';
const joinFromUrl = params.get('join');
const pairFromUrl = params.get('pair');
if (joinFromUrl) localStorage.setItem('overink.join', joinFromUrl);

function getDeviceKey() {
  return localStorage.getItem('overink.deviceKey') || '';
}
function getJoinToken() {
  return params.get('join') || localStorage.getItem('overink.join') || null;
}
function getPairToken() {
  return params.get('pair') || null;
}

let shouldReconnect = true;
let sessionReady = false;
let reconnectTimer = null;
let frameLogCount = 0;

const frameCanvas = document.getElementById('frame');
const peerCanvas = document.getElementById('peerInk');
const localCanvas = document.getElementById('localInk');
const inputSurface = document.getElementById('inputSurface');
const statusEl = document.getElementById('status');
const logEl = document.getElementById('log');
const toolsEl = document.getElementById('tools');
const settingsPanel = document.getElementById('settingsPanel');
const settingsBtn = document.getElementById('settingsBtn');
const debugLogToggle = document.getElementById('debugLogToggle');
const resetViewBtn = document.getElementById('resetViewBtn');

let debugLog = localStorage.getItem('overink.debugLog') === '1';
let settingsOpen = false;

const ctxOpts = { desynchronized: true };
const fctx = frameCanvas.getContext('2d', ctxOpts) || frameCanvas.getContext('2d');
const pctx = peerCanvas.getContext('2d', ctxOpts) || peerCanvas.getContext('2d');
const lctx = localCanvas.getContext('2d', ctxOpts) || localCanvas.getContext('2d');

let ws;
let clientId = '';
let screenW = 1920;
let screenH = 1080;
let inkRevision = 0;
let tool = 'Pen';
let color = '#E53935';
let size = 4;
let followHost = false;

const view = { scale: 1, tx: 0, ty: 0, rotation: 0 };
const MIN_ZOOM = 0.35;
const MAX_ZOOM = 8;
const rootEl = document.getElementById('root');
const inputEl = inputSurface || rootEl;
const pendingLocal = new Map();
const peerStrokes = new Map();
const activeLocal = new Map();
/** Overlap local preview with JPEG ink after ack + stream catch-up (ms). */
const LOCAL_INK_HOLD_MS = 520;
/** If no matching frame revision arrives, release preview after this (ms). */
const LOCAL_INK_FALLBACK_MS = 1400;
let frameBitmap = null;
let contentRect = { x: 0, y: 0, w: 1, h: 1 };
let batchPoints = [];
let batchScheduled = false;
let shapeDrag = null;
let pingTimer;

const colors = [
  '#E53935', '#E91E63', '#9C27B0', '#673AB7', '#3F51B5', '#1E88E5', '#00ACC1',
  '#43A047', '#CDDC39', '#FDD835', '#FB8C00', '#795548', '#FFFFFF', '#212121'
];

/** WPF uses #AARRGGBB; canvas/CSS expects #RRGGBB (8-char CSS is #RRGGBBAA). */
function normalizeColorHex(hex) {
  if (!hex || typeof hex !== 'string') return hex;
  const s = hex.trim();
  if (/^#[0-9A-Fa-f]{6}$/.test(s)) return s.toUpperCase();
  const argb = /^#([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})$/.exec(s);
  if (argb) {
    return `#${argb[2]}${argb[3]}${argb[4]}`.toUpperCase();
  }
  return s;
}

function setStatus(text) { statusEl.textContent = text; }

const logLines = [];
function canDrawPointer(e) {
  if (e.pointerType === 'pen') return true;
  if (allowMouse && e.pointerType === 'mouse') return true;
  if (allowTouchDraw && e.pointerType === 'touch') return true;
  return false;
}

function refreshLogPanel() {
  if (!debugLog) return;
  logEl.textContent = logLines.join('\n');
  logEl.scrollTop = logEl.scrollHeight;
}

function log(msg, opts = {}) {
  const line = `${new Date().toLocaleTimeString()} ${msg}`;
  logLines.push(line);
  if (logLines.length > 80) logLines.shift();
  if (debugLog || opts.alwaysShow) {
    refreshLogPanel();
  }
  if (debugLog || opts.alwaysShow) {
    console.log('[OverInk]', msg);
  }
}

function setDebugLog(enabled) {
  debugLog = enabled;
  localStorage.setItem('overink.debugLog', enabled ? '1' : '0');
  if (debugLogToggle) debugLogToggle.checked = enabled;
  logEl.classList.toggle('panel-hidden', !enabled);
  if (enabled) refreshLogPanel();
}

function setSettingsOpen(open) {
  settingsOpen = open;
  settingsPanel.classList.toggle('panel-hidden', !open);
  settingsPanel.setAttribute('aria-hidden', open ? 'false' : 'true');
}

function isUiTarget(target) {
  return target && target.closest && (
    target.closest('#tools') ||
    target.closest('#log') ||
    target.closest('#settingsPanel') ||
    target.closest('#settingsBtn') ||
    target.closest('#toolbarRow')
  );
}

function initSettingsUi() {
  setDebugLog(debugLog);
  debugLogToggle.addEventListener('change', () => setDebugLog(debugLogToggle.checked));
  resetViewBtn.addEventListener('click', () => {
    resetView();
    log('view reset');
  });
  settingsBtn.addEventListener('click', () => setSettingsOpen(!settingsOpen));
}

function resize() {
  const dpr = devicePixelRatio || 1;
  for (const c of [frameCanvas, peerCanvas, localCanvas]) {
    c.width = innerWidth * dpr;
    c.height = innerHeight * dpr;
  }
  redraw();
}
addEventListener('resize', resize);
resize();

function send(obj) {
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  ws.send(JSON.stringify(obj));
}

function connect() {
  sessionReady = false;
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }

  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  const url = `${proto}://${location.host}/ws`;
  log(`WS connect → ${url}`);
  ws = new WebSocket(url);
  ws.binaryType = 'arraybuffer';

  ws.onopen = () => {
    setStatus('Connecting… (waiting for welcome)');
    log('WS open, sending hello');
    send({
      t: 'hello', v: 1,
      deviceKey: getDeviceKey() || null,
      pair: getPairToken(),
      join: getJoinToken(),
      deviceName
    });
    pingTimer = setInterval(() => send({ t: 'ping', ms: Date.now() }), 2000);
  };

  ws.onerror = () => {
    setStatus('Connection error — check Wi‑Fi and server');
    log('WS error event');
  };

  ws.onclose = (ev) => {
    clearInterval(pingTimer);
    log(`WS close code=${ev.code} reason="${ev.reason || ''}" wasReady=${sessionReady}`);
    if (!shouldReconnect) {
      setStatus('Disconnected');
      return;
    }
    const hint = ev.code === 1006 ? ' (abnormal close)' : '';
    setStatus(`Reconnecting…${hint}`);
    reconnectTimer = setTimeout(connect, 1500);
  };

  ws.onmessage = (ev) => {
    try {
      if (ev.data instanceof ArrayBuffer) {
        onFrameBuffer(ev.data);
        return;
      }
      if (typeof Blob !== 'undefined' && ev.data instanceof Blob) {
        ev.data.arrayBuffer().then(onFrameBuffer).catch(err => log(`frame blob error: ${err.message}`));
        return;
      }
      const text = typeof ev.data === 'string' ? ev.data : '';
      if (debugLog) {
        log(`text ← ${text.slice(0, 120)}${text.length > 120 ? '…' : ''}`);
      }
      const msg = JSON.parse(text);
      handleJson(msg);
    } catch (err) {
      log(`message error: ${err.message}`);
      console.error('message handling failed', err);
    }
  };
}

function handleJson(msg) {
  switch (msg.t) {
    case 'welcome':
      sessionReady = true;
      log(`welcome clientId=${msg.clientId} screen=${msg.screenW}x${msg.screenH}`);
      clientId = msg.clientId;
      screenW = msg.screenW; screenH = msg.screenH;
      inkRevision = msg.inkRevision || 0;
      if (msg.deviceKey) {
        localStorage.setItem('overink.deviceKey', msg.deviceKey);
      }
      if (msg.state) applyState(msg.state);
      setStatus(`Ready · ${Math.round(screenW)}×${Math.round(screenH)} · 2-finger pinch/rotate · finger pan`);
      try {
        inputEl.focus({ preventScroll: true });
      } catch {
        inputEl.focus();
      }
      break;
    case 'state':
      applyState(msg);
      break;
    case 'ack':
      finalizeLocal(msg.id, msg.inkRevision);
      break;
    case 'pong':
      if (!sessionReady) {
        setStatus(`Connected · ${Date.now() - (msg.ms || 0)} ms`);
      }
      break;
    case 'error':
      log(`server error ${msg.code}: ${msg.message}`);
      setStatus(`${msg.message} (${msg.code})`);
      if (msg.code === 'badToken') {
        localStorage.removeItem('overink.deviceKey');
      }
      if (['badToken', 'denied', 'versionMismatch', 'sessionFull', 'badHello'].includes(msg.code)) {
        shouldReconnect = false;
        try { ws.close(); } catch { /* ignore */ }
        if (msg.code === 'badToken') {
          setStatus('Session expired — open Remote sketch pair on PC and scan QR again.');
        }
      }
      break;
    case 'peerStrokeBegin':
      startPeerStroke(msg);
      break;
    case 'peerStrokePoints':
      appendPeer(msg.id, msg.pts);
      break;
    case 'peerStrokeEnd':
    case 'peerStrokeCancel':
      peerStrokes.delete(msg.id);
      redraw();
      break;
    case 'peerShapePreview':
    case 'peerShapeCommit':
      drawPeerShape(msg);
      break;
    case 'roster':
      break;
    default:
      break;
  }
}

function applyState(state) {
  tool = state.tool || tool;
  if (state.color) color = normalizeColorHex(state.color);
  size = state.size || size;
  if (state.screenW > 0 && state.screenH > 0) {
    screenW = state.screenW;
    screenH = state.screenH;
  }
  buildTools();
}

function onFrameBuffer(buffer) {
  frameLogCount += 1;
  if (frameLogCount <= 3 || frameLogCount % 30 === 0) {
    log(`frame #${frameLogCount} ${buffer.byteLength} bytes`);
  }
  handleFrame(buffer);
}

function handleFrame(buffer) {
  if (buffer.byteLength < 17) {
    log(`frame too short: ${buffer.byteLength}`);
    return;
  }
  const view8 = new DataView(buffer);
  if (view8.getUint8(0) !== 1) return;
  const rev = Number(view8.getBigInt64(5, true));
  inkRevision = rev;
  const streamW = view8.getUint16(13, true);
  const streamH = view8.getUint16(15, true);
  if (frameLogCount <= 1) {
    log(`stream ${streamW}×${streamH} jpeg ${buffer.byteLength - 17}B`);
  }
  const jpeg = buffer.slice(17);
  if (jpeg.byteLength < 4) {
    log('frame jpeg empty');
    return;
  }
  const u8 = new Uint8Array(jpeg);
  if (u8[0] !== 0xff || u8[1] !== 0xd8) {
    log(`frame bad jpeg magic ${u8[0]},${u8[1]}`);
    return;
  }
  decodeJpeg(jpeg).then(bmp => {
    frameBitmap = bmp;
    tryReleaseLocalForFrame(rev);
    redraw();
  }).catch(err => log(`jpeg decode failed: ${err.message}`));
}

function decodeJpeg(jpegBytes) {
  const blob = new Blob([jpegBytes], { type: 'image/jpeg' });
  if (typeof createImageBitmap === 'function') {
    return createImageBitmap(blob).catch(() => decodeJpegViaImage(blob));
  }
  return decodeJpegViaImage(blob);
}

function decodeJpegViaImage(blob) {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(blob);
    const img = new Image();
    img.onload = () => {
      URL.revokeObjectURL(url);
      resolve(img);
    };
    img.onerror = () => {
      URL.revokeObjectURL(url);
      reject(new Error('Image decode failed'));
    };
    img.src = url;
  });
}

function updateContentRect() {
  if (!frameBitmap) {
    contentRect = { x: 0, y: 0, w: innerWidth, h: innerHeight };
    return;
  }
  const bw = frameBitmap.width;
  const bh = frameBitmap.height;
  const scale = Math.min(innerWidth / bw, innerHeight / bh);
  contentRect = {
    w: bw * scale,
    h: bh * scale,
    x: (innerWidth - bw * scale) / 2,
    y: (innerHeight - bh * scale) / 2
  };
}

function scheduleLocalInkRelease(id) {
  const meta = pendingLocal.get(id);
  if (!meta || meta.releaseTimer || !activeLocal.has(id)) {
    return;
  }
  meta.releaseTimer = setTimeout(() => {
    pendingLocal.delete(id);
    if (activeLocal.delete(id)) {
      redraw();
    }
  }, LOCAL_INK_HOLD_MS);
}

function tryReleaseLocalForFrame(rev) {
  for (const [id, meta] of pendingLocal.entries()) {
    if (!activeLocal.has(id)) {
      pendingLocal.delete(id);
      continue;
    }
    if (meta.targetRev > 0 && rev >= meta.targetRev) {
      scheduleLocalInkRelease(id);
    }
  }
}

function finalizeLocal(id, rev) {
  if (!activeLocal.has(id)) {
    pendingLocal.delete(id);
    return;
  }

  const targetRev = typeof rev === 'number' ? rev : 0;
  const prev = pendingLocal.get(id);
  if (prev?.releaseTimer) {
    clearTimeout(prev.releaseTimer);
  }
  if (prev?.fallbackTimer) {
    clearTimeout(prev.fallbackTimer);
  }

  pendingLocal.set(id, {
    since: prev?.since ?? Date.now(),
    targetRev,
    ackAt: Date.now(),
    releaseTimer: null,
    fallbackTimer: setTimeout(() => {
      if (pendingLocal.has(id) && activeLocal.has(id)) {
        scheduleLocalInkRelease(id);
      }
    }, LOCAL_INK_FALLBACK_MS)
  });

  if (inkRevision >= targetRev && targetRev > 0) {
    scheduleLocalInkRelease(id);
  }
}

function canvasClientOffset() {
  const rect = frameCanvas.getBoundingClientRect();
  return { left: rect.left, top: rect.top };
}

/** Screen client coords → inner view space (before contentRect), inverse of applyViewTransform. */
function clientToInnerView(clientX, clientY) {
  const { left, top } = canvasClientOffset();
  let x = clientX - left - view.tx;
  let y = clientY - top - view.ty;
  const c = Math.cos(-view.rotation);
  const s = Math.sin(-view.rotation);
  const xr = c * x - s * y;
  const yr = s * x + c * y;
  return [xr / view.scale, yr / view.scale];
}

function applyViewTransform(ctx) {
  ctx.translate(view.tx, view.ty);
  ctx.rotate(view.rotation);
  ctx.scale(view.scale, view.scale);
}

function normFromClient(x, y) {
  const [cx, cy] = clientToInnerView(x, y);
  updateContentRect();
  const nx = (cx - contentRect.x) / contentRect.w;
  const ny = (cy - contentRect.y) / contentRect.h;
  return [Math.max(0, Math.min(1, nx)), Math.max(0, Math.min(1, ny))];
}

function canvasFromNorm(nx, ny) {
  updateContentRect();
  return [
    contentRect.x + nx * contentRect.w,
    contentRect.y + ny * contentRect.h
  ];
}

function redraw() {
  const dpr = devicePixelRatio || 1;
  fctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  pctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  lctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  fctx.clearRect(0, 0, innerWidth, innerHeight);
  pctx.clearRect(0, 0, innerWidth, innerHeight);
  lctx.clearRect(0, 0, innerWidth, innerHeight);

  fctx.save();
  applyViewTransform(fctx);
  updateContentRect();
  if (frameBitmap) {
    fctx.drawImage(frameBitmap, contentRect.x, contentRect.y, contentRect.w, contentRect.h);
  } else {
    fctx.fillStyle = '#222';
    fctx.fillRect(0, 0, innerWidth, innerHeight);
  }
  fctx.restore();

  drawStrokeMap(pctx, peerStrokes);
  drawStrokeMap(lctx, activeLocal);
}

function drawStrokeMap(ctx, map) {
  for (const stroke of map.values()) {
    drawStroke(ctx, stroke);
  }
}

function drawStroke(ctx, stroke) {
  const pts = stroke.pts;
  if (pts.length < 3) return;
  ctx.save();
  applyViewTransform(ctx);
  ctx.strokeStyle = normalizeColorHex(stroke.color);
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  ctx.beginPath();
  for (let i = 0; i < pts.length; i += 3) {
    const [x, y] = canvasFromNorm(pts[i], pts[i + 1]);
    const base = stroke.size / Math.max(0.25, view.scale);
    const w = base * (0.4 + 0.6 * (pts[i + 2] ?? 1));
    ctx.lineWidth = Math.max(1, w);
    if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
  }
  ctx.stroke();
  ctx.restore();
}

function startPeerStroke(msg) {
  peerStrokes.set(msg.id, { color: normalizeColorHex(msg.color), size: msg.size, pts: [msg.x, msg.y, msg.p ?? 1] });
  redraw();
}

function appendPeer(id, pts) {
  const s = peerStrokes.get(id);
  if (!s) return;
  s.pts.push(...pts);
  redraw();
}

function drawPeerShape(msg) {
  pctx.save();
  applyViewTransform(pctx);
  const [x1, y1] = canvasFromNorm(msg.x1, msg.y1);
  const [x2, y2] = canvasFromNorm(msg.x2, msg.y2);
  pctx.strokeStyle = normalizeColorHex(msg.color);
  pctx.lineWidth = Math.max(1, msg.size);
  pctx.beginPath();
  if (msg.tool === 'Rectangle') {
    pctx.strokeRect(Math.min(x1, x2), Math.min(y1, y2), Math.abs(x2 - x1), Math.abs(y2 - y1));
  } else if (msg.tool === 'Ellipse') {
    pctx.ellipse((x1 + x2) / 2, (y1 + y2) / 2, Math.abs(x2 - x1) / 2, Math.abs(y2 - y1) / 2, 0, 0, Math.PI * 2);
    pctx.stroke();
  } else {
    pctx.moveTo(x1, y1);
    pctx.lineTo(x2, y2);
    pctx.stroke();
  }
  pctx.restore();
}

let batchStrokeId = null;
function flushStrokePoints(id) {
  if (!batchPoints.length || batchStrokeId !== id) {
    return;
  }
  send({ t: 'strokePoints', id, pts: batchPoints.slice() });
  batchPoints = [];
  batchScheduled = false;
}

function scheduleBatch(id) {
  batchStrokeId = id;
  if (batchScheduled) return;
  batchScheduled = true;
  requestAnimationFrame(() => {
    batchScheduled = false;
    if (batchPoints.length && batchStrokeId) {
      send({ t: 'strokePoints', id: batchStrokeId, pts: batchPoints.slice() });
      batchPoints = [];
    }
  });
}

let drawPointerId = null;
let drawPointerErasing = false;
/** @type {number | null} */
let stylusDrawTouchId = null;
const windowPointerOpts = { passive: false };


function armWindowPointer() {
  window.addEventListener('pointermove', pointerMove, windowPointerOpts);
  window.addEventListener('pointerup', pointerUp, windowPointerOpts);
  window.addEventListener('pointercancel', pointerUp, windowPointerOpts);
}

function disarmWindowPointer() {
  window.removeEventListener('pointermove', pointerMove, windowPointerOpts);
  window.removeEventListener('pointerup', pointerUp, windowPointerOpts);
  window.removeEventListener('pointercancel', pointerUp, windowPointerOpts);
}

function capturePointer(e) {
  try {
    inputEl.setPointerCapture(e.pointerId);
  } catch {
    /* iOS may reject capture for some pointer types */
  }
}

function isDrawAreaTarget(target) {
  if (!target || !target.closest) return false;
  if (target.closest('#tools') || target.closest('#log')) return false;
  return Boolean(target.closest('#inputSurface') || target.closest('#root'));
}

function pressureFromPointer(e) {
  if (e.pointerType === 'pen' && e.pressure > 0) return e.pressure;
  if (e.pointerType === 'mouse') return 0.5;
  return e.pressure || 0.5;
}

function pressureFromTouch(t) {
  if (typeof t.force === 'number' && t.force > 0) return Math.min(1, t.force);
  return 0.5;
}

function newStrokeId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    try {
      return crypto.randomUUID();
    } catch {
      /* http:// LAN is not a secure context on iPad */
    }
  }
  const bytes = new Uint8Array(16);
  if (typeof crypto !== 'undefined' && crypto.getRandomValues) {
    crypto.getRandomValues(bytes);
  } else {
    for (let i = 0; i < 16; i++) bytes[i] = Math.floor(Math.random() * 256);
  }
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const h = [...bytes].map(b => b.toString(16).padStart(2, '0')).join('');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

function beginDrawAtClient(clientX, clientY, p, source) {
  const [nx, ny] = normFromClient(clientX, clientY);
  const id = newStrokeId();

  if (tool === 'Eraser') {
    drawPointerErasing = true;
    send({ t: 'erase', pts: [nx, ny], size });
    return;
  }
  drawPointerErasing = false;

  if (['Line', 'Arrow', 'Rectangle', 'Ellipse'].includes(tool)) {
    shapeDrag = { id, x1: nx, y1: ny, x2: nx, y2: ny };
    return;
  }

  activeLocal.set(id, { color: normalizeColorHex(color), size, pts: [nx, ny, p] });
  frameCanvas._activeStrokeId = id;
  log(`draw begin ${tool} ${source} (${nx.toFixed(3)},${ny.toFixed(3)})`);
  send({ t: 'strokeBegin', id, color, size, x: nx, y: ny, p });
}

function continueDrawAtClient(clientX, clientY, p) {
  if (shapeDrag) {
    const [nx, ny] = normFromClient(clientX, clientY);
    shapeDrag.x2 = nx;
    shapeDrag.y2 = ny;
    send({ t: 'shapePreview', id: shapeDrag.id, tool, x1: shapeDrag.x1, y1: shapeDrag.y1, x2: nx, y2: ny, color, size });
    redraw();
    return;
  }

  const strokeId = frameCanvas._activeStrokeId;
  const stroke = strokeId ? activeLocal.get(strokeId) : null;
  if (stroke) {
    const [nx, ny] = normFromClient(clientX, clientY);
    stroke.pts.push(nx, ny, p);
    batchPoints.push(nx, ny, p);
    scheduleBatch(strokeId);
  }
  if (tool === 'Eraser') {
    const [nx, ny] = normFromClient(clientX, clientY);
    send({ t: 'erase', pts: [nx, ny], size });
  }
  redraw();
}

function continueDraw(e) {
  const events = e.getCoalescedEvents ? e.getCoalescedEvents() : [e];
  for (const ev of events) {
    continueDrawAtClient(ev.clientX, ev.clientY, pressureFromPointer(ev));
  }
}

function endDraw() {
  if (shapeDrag) {
    send({
      t: 'shapeCommit', id: shapeDrag.id, tool,
      x1: shapeDrag.x1, y1: shapeDrag.y1, x2: shapeDrag.x2, y2: shapeDrag.y2, color, size
    });
    shapeDrag = null;
    return;
  }
  const id = frameCanvas._activeStrokeId;
  if (id && activeLocal.has(id)) {
    flushStrokePoints(id);
    send({ t: 'strokeEnd', id });
    log(`draw end id=${id.slice(0, 8)}…`);
    pendingLocal.set(id, { since: Date.now(), targetRev: 0, ackAt: 0, releaseTimer: null, fallbackTimer: null });
    frameCanvas._activeStrokeId = null;
    setTimeout(() => {
      if (pendingLocal.has(id) && activeLocal.has(id) && !pendingLocal.get(id)?.releaseTimer) {
        log(`ack timeout id=${id.slice(0, 8)}… (kept local preview)`);
        scheduleLocalInkRelease(id);
      }
    }, 8000);
  }
  redraw();
}

function pointerDown(e) {
  if (!sessionReady || isUiTarget(e.target)) return;
  if (!isDrawAreaTarget(e.target)) return;
  if (drawPointerId !== null || stylusDrawTouchId !== null) return;
  if (!canDrawPointer(e)) return;
  if (touchGesture && e.pointerType !== 'pen') return;

  e.preventDefault();
  e.stopPropagation();
  drawPointerId = e.pointerId;
  capturePointer(e);
  armWindowPointer();
  beginDrawAtClient(e.clientX, e.clientY, pressureFromPointer(e), e.pointerType);
}

/** Safari often skips canvas; catch pen at document capture before Scribble/UI eats it. */
function onPointerDownCapture(e) {
  if (e.pointerType !== 'pen') return;
  if (!sessionReady) return;
  if (debugLog) log(`pen hit ${e.target.id || e.target.nodeName}`);
  if (isUiTarget(e.target)) return;
  if (!isDrawAreaTarget(e.target)) return;
  if (drawPointerId !== null || stylusDrawTouchId !== null) return;
  pointerDown(e);
  e.stopImmediatePropagation();
}

function pointerMove(e) {
  if (drawPointerId === null || e.pointerId !== drawPointerId) return;
  if (!frameCanvas._activeStrokeId && !shapeDrag && !drawPointerErasing) return;
  e.preventDefault();
  continueDraw(e);
}

function pointerUp(e) {
  if (drawPointerId !== null && e.pointerId !== drawPointerId) return;
  drawPointerId = null;
  drawPointerErasing = false;
  disarmWindowPointer();
  endDraw();
}

function onPointerRawUpdate(e) {
  if (drawPointerId !== e.pointerId) return;
  if (!frameCanvas._activeStrokeId && !shapeDrag && !drawPointerErasing) return;
  e.preventDefault();
  continueDraw(e);
}

function clampZoom(s) {
  return Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, s));
}

function zoomAt(clientX, clientY, scaleFactor) {
  const [ix, iy] = clientToInnerView(clientX, clientY);
  view.scale = clampZoom(view.scale * scaleFactor);
  const { left, top } = canvasClientOffset();
  const px = clientX - left;
  const py = clientY - top;
  const sx = view.scale * (Math.cos(view.rotation) * ix - Math.sin(view.rotation) * iy);
  const sy = view.scale * (Math.sin(view.rotation) * ix + Math.cos(view.rotation) * iy);
  view.tx = px - sx;
  view.ty = py - sy;
  redraw();
}

function rotateViewAboutScreenPoint(clientX, clientY, deltaAngle) {
  if (Math.abs(deltaAngle) < 0.0001) return;
  const [ix, iy] = clientToInnerView(clientX, clientY);
  view.rotation += deltaAngle;
  const { left, top } = canvasClientOffset();
  const px = clientX - left;
  const py = clientY - top;
  const sx = view.scale * (Math.cos(view.rotation) * ix - Math.sin(view.rotation) * iy);
  const sy = view.scale * (Math.sin(view.rotation) * ix + Math.cos(view.rotation) * iy);
  view.tx = px - sx;
  view.ty = py - sy;
}

function resetView() {
  view.scale = 1;
  view.tx = 0;
  view.ty = 0;
  view.rotation = 0;
  redraw();
}

/** @type {null | { kind: 'pan' | 'pinch', lastX?: number, lastY?: number, lastDist?: number }} */
let touchGesture = null;

function touchDistance(touches) {
  const dx = touches[0].clientX - touches[1].clientX;
  const dy = touches[0].clientY - touches[1].clientY;
  return Math.hypot(dx, dy);
}

function touchAngle(touches) {
  return Math.atan2(
    touches[0].clientY - touches[1].clientY,
    touches[0].clientX - touches[1].clientX
  );
}

function normalizeAngleDelta(delta) {
  while (delta > Math.PI) delta -= 2 * Math.PI;
  while (delta < -Math.PI) delta += 2 * Math.PI;
  return delta;
}

function isStylusTouch(touch) {
  if (touch.touchType === 'stylus' || touch.touchType === 'pen') return true;
  // Rare: touchType missing — Pencil contact area is much smaller than a finger.
  if (!touch.touchType && typeof touch.radiusX === 'number' && touch.radiusX <= 0.75) {
    return true;
  }
  return false;
}

function startStylusDraw(touch, e) {
  if (!sessionReady) {
    log('stylus blocked: not ready');
    return;
  }
  if (isUiTarget(e.target)) return;
  if (drawPointerId !== null) return;
  e.preventDefault();
  touchGesture = null;
  stylusDrawTouchId = touch.identifier;
  try {
    beginDrawAtClient(touch.clientX, touch.clientY, pressureFromTouch(touch), 'stylus');
  } catch (err) {
    stylusDrawTouchId = null;
    log(`stylus draw error: ${err.message}`);
  }
}

function onTouchStart(e) {
  if (isUiTarget(e.target)) return;
  if (e.touches.length === 2) {
    const ang = touchAngle(e.touches);
    touchGesture = {
      kind: 'pinch',
      lastDist: touchDistance(e.touches),
      lastMidX: (e.touches[0].clientX + e.touches[1].clientX) / 2,
      lastMidY: (e.touches[0].clientY + e.touches[1].clientY) / 2,
      lastAngle: ang
    };
    return;
  }
  if (e.touches.length === 1) {
    const t = e.touches[0];
    if (isStylusTouch(t)) {
      startStylusDraw(t, e);
      return;
    }
    if (!allowTouchDraw) {
      touchGesture = {
        kind: 'pan',
        lastX: t.clientX,
        lastY: t.clientY
      };
      return;
    }
  }
  touchGesture = null;
}

function onTouchMove(e) {
  if (stylusDrawTouchId !== null) {
    for (let i = 0; i < e.touches.length; i++) {
      const t = e.touches[i];
      if (t.identifier === stylusDrawTouchId) {
        e.preventDefault();
        continueDrawAtClient(t.clientX, t.clientY, pressureFromTouch(t));
        return;
      }
    }
    for (let i = 0; i < e.changedTouches.length; i++) {
      const t = e.changedTouches[i];
      if (t.identifier === stylusDrawTouchId) {
        e.preventDefault();
        continueDrawAtClient(t.clientX, t.clientY, pressureFromTouch(t));
        return;
      }
    }
  }
  if (!touchGesture) return;
  if (e.touches.length === 2 && touchGesture.kind === 'pinch') {
    e.preventDefault();
    const midX = (e.touches[0].clientX + e.touches[1].clientX) / 2;
    const midY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
    const dist = touchDistance(e.touches);
    const angle = touchAngle(e.touches);
    if (touchGesture.lastAngle !== undefined) {
      const delta = normalizeAngleDelta(angle - touchGesture.lastAngle);
      rotateViewAboutScreenPoint(midX, midY, delta);
    }
    if (touchGesture.lastDist > 0) {
      const factor = dist / touchGesture.lastDist;
      zoomAt(midX, midY, factor);
    }
    view.tx += midX - touchGesture.lastMidX;
    view.ty += midY - touchGesture.lastMidY;
    touchGesture.lastDist = dist;
    touchGesture.lastMidX = midX;
    touchGesture.lastMidY = midY;
    touchGesture.lastAngle = angle;
    redraw();
    return;
  }
  if (e.touches.length === 1 && touchGesture.kind === 'pan') {
    e.preventDefault();
    const t = e.touches[0];
    view.tx += t.clientX - touchGesture.lastX;
    view.ty += t.clientY - touchGesture.lastY;
    touchGesture.lastX = t.clientX;
    touchGesture.lastY = t.clientY;
    redraw();
  }
}

function onTouchEnd(e) {
  if (stylusDrawTouchId !== null) {
    let stylusStillDown = false;
    for (let i = 0; i < e.touches.length; i++) {
      if (e.touches[i].identifier === stylusDrawTouchId) stylusStillDown = true;
    }
    for (let i = 0; i < e.changedTouches.length; i++) {
      if (e.changedTouches[i].identifier === stylusDrawTouchId && !stylusStillDown) {
        stylusDrawTouchId = null;
        drawPointerErasing = false;
        endDraw();
      }
    }
  }
  if (e.touches.length === 0) {
    touchGesture = null;
    return;
  }
  if (e.touches.length === 1 && stylusDrawTouchId === null) {
    const t = e.touches[0];
    if (!isStylusTouch(t) && !allowTouchDraw) {
      touchGesture = {
        kind: 'pan',
        lastX: t.clientX,
        lastY: t.clientY
      };
    }
  }
}

function buildTools() {
  toolsEl.innerHTML = '';
  for (const name of ['Pen','Line','Arrow','Rectangle','Ellipse','Eraser']) {
    const b = document.createElement('button');
    b.className = 'tool-btn' + (tool === name ? ' active' : '');
    b.textContent = name;
    b.onclick = () => { tool = name; if (!followHost) send({ t: 'setTool', tool: name }); buildTools(); };
    toolsEl.appendChild(b);
  }
  for (const c of colors) {
    const s = document.createElement('button');
    s.className = 'sw tool-btn';
    s.style.background = c;
    s.onclick = () => {
      color = normalizeColorHex(c);
      if (!followHost) send({ t: 'setColor', color });
    };
    toolsEl.appendChild(s);
  }
  const undo = document.createElement('button'); undo.className = 'tool-btn'; undo.textContent = 'Undo';
  undo.onclick = () => send({ t: 'undo' });
  const redo = document.createElement('button'); redo.className = 'tool-btn'; redo.textContent = 'Redo';
  redo.onclick = () => send({ t: 'redo' });
  toolsEl.appendChild(undo); toolsEl.appendChild(redo);
}

for (const type of ['pointerdown', 'pointermove', 'pointerup', 'pointercancel']) {
  inputEl.addEventListener(type, type === 'pointerdown' ? pointerDown : type === 'pointermove' ? pointerMove : pointerUp, { passive: false });
}
if ('onpointerrawupdate' in window) {
  inputEl.addEventListener('pointerrawupdate', onPointerRawUpdate, { passive: false });
}
inputEl.addEventListener('wheel', (e) => {
  e.preventDefault();
  zoomAt(e.clientX, e.clientY, e.deltaY < 0 ? 1.1 : 1 / 1.1);
}, { passive: false });

document.addEventListener('pointerdown', onPointerDownCapture, true);

const touchOpts = { passive: false, capture: true };
document.addEventListener('touchstart', onTouchStart, touchOpts);
document.addEventListener('touchmove', onTouchMove, touchOpts);
document.addEventListener('touchend', onTouchEnd, touchOpts);
document.addEventListener('touchcancel', onTouchEnd, touchOpts);

buildTools();
initSettingsUi();
log(`page v10`, { alwaysShow: debugLog });
connect();
