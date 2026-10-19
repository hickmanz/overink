import { icon } from './icons.js?v=2';

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
const statusChip = document.getElementById('statusChip');
const statusIconEl = document.getElementById('statusIcon');
const logEl = document.getElementById('log');
const toolsEl = document.getElementById('tools');
const settingsPanel = document.getElementById('settingsPanel');
const settingsBtn = document.getElementById('settingsBtn');
const debugLogToggle = document.getElementById('debugLogToggle');
const resetViewBtn = document.getElementById('resetViewBtn');
const colorBtn = document.getElementById('colorBtn');
const colorDot = document.getElementById('colorDot');
const colorPopover = document.getElementById('colorPopover');
const swatchesEl = document.getElementById('swatches');
const sizeBtn = document.getElementById('sizeBtn');
const sizeDot = document.getElementById('sizeDot');
const sizePopover = document.getElementById('sizePopover');
const sizeSlider = document.getElementById('sizeSlider');
const pressureToggle = document.getElementById('pressureToggle');
const sizeValue = document.getElementById('sizeValue');
const sizePreview = document.getElementById('sizePreview');
const undoBtn = document.getElementById('undoBtn');
const redoBtn = document.getElementById('redoBtn');
const clearBtn = document.getElementById('clearBtn');

let debugLog = localStorage.getItem('overink.debugLog') === '1';
/** Which popover is open: 'color' | 'size' | 'settings' | null. */
let openPopover = null;

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
/** Whether pen pressure changes stroke width. Shared with the PC (it is the source of truth). */
let pressureSensitive = true;

const view = { scale: 1, tx: 0, ty: 0, rotation: 0 };
const MIN_ZOOM = 0.35;
const MAX_ZOOM = 8;
const rootEl = document.getElementById('root');
const inputEl = inputSurface || rootEl;
const pendingLocal = new Map();
const peerStrokes = new Map();
const activeLocal = new Map();
/** Committed pen strokes — always drawn as vectors over the JPEG for crisp ink. */
const vectorInk = new Map();
/**
 * Shapes committed from this device, drawn locally for instant feedback until a streamed frame that
 * already contains them arrives (or a timeout). Never kept long-term, so clear/undo/erase on the PC
 * can't leave stale copies behind.
 */
const localShapes = new Map();
/** Shapes from other participants (preview while dragging, brief hold after commit). */
const peerShapes = new Map();
const LOCAL_SHAPE_TIMEOUT_MS = 4000;
/** The PC may need a render tick after bumping its revision before pixels show the shape. */
const LOCAL_SHAPE_RENDER_GRACE_MS = 250;
/** Eraser footprint under the finger/pen: { nx, ny } while erasing. */
let eraserCursor = null;
/** Eraser gesture state: { whole, started, cx, cy, nx, ny, prevNx, prevNy } (client px / normalized). */
let eraseGesture = null;
/** Last completed eraser tap, waiting to see if a second tap follows (double tap = erase whole stroke). */
let lastEraseTap = null;
const DOUBLE_TAP_MS = 350;
const DOUBLE_TAP_SLOP_PX = 30;
/** Pointer travel (client px) before a press counts as a drag rather than a tap. */
const ERASE_DRAG_SLOP_PX = 8;
let localPieceSeq = 0;
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

const STATUS_ICONS = { ok: 'wifi', warn: 'loader', connecting: 'loader', error: 'wifiOff' };

/** state: 'connecting' | 'ok' | 'warn' | 'error' */
function setStatus(text, state = 'connecting') {
  statusEl.textContent = text;
  statusChip.dataset.state = state;
  statusIconEl.innerHTML = icon(STATUS_ICONS[state] || 'loader', 16);
}

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

const hudEl = document.getElementById('hud');
const dockEl = document.getElementById('dock');
const dockGrip = document.getElementById('dockGrip');
const snapZonesEl = document.getElementById('snapZones');

const DOCK_EDGES = ['bottom', 'left', 'right'];
let dockEdge = localStorage.getItem('overink.dockEdge');
if (!DOCK_EDGES.includes(dockEdge)) dockEdge = 'bottom';

const POPOVERS = {
  color: { panel: colorPopover, button: colorBtn },
  size: { panel: sizePopover, button: sizeBtn },
  settings: { panel: settingsPanel, button: settingsBtn }
};

/** Anchor a fixed popover beside its dock button (above it on the bottom dock), clamped to the viewport. */
function positionPopover(name) {
  const { panel, button } = POPOVERS[name];
  const r = button.getBoundingClientRect();
  const pw = panel.offsetWidth;
  const ph = panel.offsetHeight;
  const margin = 12;
  const gap = 12;
  let left;
  let top;
  if (dockEdge === 'left') {
    left = r.right + gap;
    top = r.top + r.height / 2 - ph / 2;
  } else if (dockEdge === 'right') {
    left = r.left - pw - gap;
    top = r.top + r.height / 2 - ph / 2;
  } else {
    left = r.left + r.width / 2 - pw / 2;
    top = r.top - ph - gap;
  }
  left = Math.max(margin, Math.min(innerWidth - pw - margin, left));
  top = Math.max(margin, Math.min(innerHeight - ph - margin, top));
  panel.style.left = `${left}px`;
  panel.style.top = `${top}px`;
  panel.style.bottom = 'auto';
}

function setPopover(name) {
  if (name === openPopover) name = null;
  openPopover = name;
  for (const [key, { panel, button }] of Object.entries(POPOVERS)) {
    const open = key === name;
    panel.classList.toggle('panel-hidden', !open);
    panel.setAttribute('aria-hidden', open ? 'false' : 'true');
    button.classList.toggle('open', open);
  }
  if (name) positionPopover(name);
}

function closePopover() {
  if (openPopover) setPopover(openPopover);
}

addEventListener('resize', () => { if (openPopover) positionPopover(openPopover); });

function isUiTarget(target) {
  return Boolean(target && target.closest && target.closest('.ui'));
}

function initSettingsUi() {
  setDebugLog(debugLog);
  debugLogToggle.addEventListener('change', () => setDebugLog(debugLogToggle.checked));
  resetViewBtn.addEventListener('click', () => {
    resetView();
    log('view reset');
  });
  settingsBtn.addEventListener('click', () => setPopover('settings'));
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
    setStatus('Connecting…', 'connecting');
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
    setStatus('Connection error — check Wi‑Fi', 'error');
    log('WS error event');
  };

  ws.onclose = (ev) => {
    clearInterval(pingTimer);
    log(`WS close code=${ev.code} reason="${ev.reason || ''}" wasReady=${sessionReady}`);
    if (!shouldReconnect) {
      setStatus('Disconnected', 'error');
      return;
    }
    const hint = ev.code === 1006 ? ' (abnormal close)' : '';
    setStatus(`Reconnecting…${hint}`, 'warn');
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
      clearVectorInkLayers();
      if (msg.deviceKey) {
        localStorage.setItem('overink.deviceKey', msg.deviceKey);
      }
      if (msg.state) applyState(msg.state);
      setStatus(`Connected · ${Math.round(screenW)}×${Math.round(screenH)}`, 'ok');
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
    case 'strokesRemoved':
      // Erased / undone / cleared on the PC: drop our vector copies so they don't linger on top of the stream.
      for (const id of msg.ids || []) {
        if (id === frameCanvas._activeStrokeId) continue;
        vectorInk.delete(id);
        // Temporary pieces we cut locally from this stroke; the PC's authoritative pieces follow in strokesAdded.
        for (const [k, st] of [...vectorInk]) {
          if (st.tmpOf === id) vectorInk.delete(k);
        }
        peerStrokes.delete(id);
        activeLocal.delete(id);
        pendingLocal.delete(id);
      }
      redraw();
      break;
    case 'strokesAdded':
      for (const s of msg.strokes || []) {
        if (!s.pts || s.pts.length < 6) continue;
        vectorInk.set(s.id, {
          color: normalizeColorHex(s.color), size: s.size, pts: s.pts, presmoothed: true,
          // Width mode the PC drew this stroke with (undefined from older PCs = follow current setting).
          usePressure: typeof s.ignorePressure === 'boolean' ? !s.ignorePressure : undefined
        });
      }
      redraw();
      break;
    case 'cleared':
      clearVectorInkLayers();
      redraw();
      break;
    case 'pong':
      if (sessionReady) {
        const ms = Math.max(0, Date.now() - (msg.ms || 0));
        setStatus(`${Math.round(screenW)}×${Math.round(screenH)} · ${ms} ms`, ms > 250 ? 'warn' : 'ok');
      }
      break;
    case 'error':
      log(`server error ${msg.code}: ${msg.message}`);
      setStatus(`${msg.message} (${msg.code})`, 'error');
      if (msg.code === 'badToken') {
        localStorage.removeItem('overink.deviceKey');
      }
      if (['badToken', 'denied', 'versionMismatch', 'sessionFull', 'badHello'].includes(msg.code)) {
        shouldReconnect = false;
        try { ws.close(); } catch { /* ignore */ }
        if (msg.code === 'badToken') {
          setStatus('Session expired — scan the QR code on the PC again', 'error');
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
      commitPeerStrokeToVector(msg.id);
      break;
    case 'peerStrokeCancel':
      peerStrokes.delete(msg.id);
      vectorInk.delete(msg.id);
      redraw();
      break;
    case 'peerShapePreview':
      setPeerShape(msg, false);
      break;
    case 'peerShapeCommit':
      setPeerShape(msg, true);
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
  if (state.size && !sizeDragging) size = state.size;
  if (typeof state.pressureSensitive === 'boolean') pressureSensitive = state.pressureSensitive;
  if (state.screenW > 0 && state.screenH > 0) {
    screenW = state.screenW;
    screenH = state.screenH;
  }
  updateUi();
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
    pruneLocalShapes(rev);
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

function clearVectorInkLayers() {
  vectorInk.clear();
  localShapes.clear();
  peerShapes.clear();
  activeLocal.clear();
  peerStrokes.clear();
  pendingLocal.clear();
}

function commitStrokeToVector(id, stroke) {
  if (!stroke || stroke.pts.length < 3) {
    return;
  }
  vectorInk.set(id, stroke);
}

function commitLocalStrokeToVector(id) {
  const stroke = activeLocal.get(id);
  if (stroke) {
    commitStrokeToVector(id, stroke);
    activeLocal.delete(id);
  }
  pendingLocal.delete(id);
}

function commitPeerStrokeToVector(id) {
  const stroke = peerStrokes.get(id);
  if (stroke) {
    commitStrokeToVector(id, stroke);
    peerStrokes.delete(id);
  }
  redraw();
}

function finalizeLocal(id, rev) {
  const shape = localShapes.get(id);
  if (shape) {
    // Keep drawing it until a streamed frame at/after this revision contains it.
    shape.ackRev = Number(rev) || 0;
    shape.ackAt = Date.now();
    setTimeout(() => { pruneLocalShapes(inkRevision); redraw(); }, LOCAL_SHAPE_RENDER_GRACE_MS + 50);
  }
  commitLocalStrokeToVector(id);
  redraw();
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
  for (const shape of peerShapes.values()) drawShape(pctx, shape);
  for (const shape of localShapes.values()) drawShape(lctx, shape);
  drawStrokeMap(lctx, vectorInk);
  drawStrokeMap(lctx, activeLocal);
  if (shapeDrag) drawShape(lctx, { ...shapeDrag, tool, color, size });
  drawEraserCursor(lctx);
}

function drawStrokeMap(ctx, map) {
  for (const stroke of map.values()) {
    drawStroke(ctx, stroke);
  }
}

/*
 * Stroke smoothing (port of StrokeSmoother.cs on the PC side).
 * Raw pen samples are sparse, so drawn as straight segments they look like a polygon once zoomed in.
 * Instead: centripetal Catmull-Rom through the samples, resample densely, low-pass blur (strength scales
 * with sample spacing), and clamp how far any point may move from the raw path.
 * Works in DIP space (norm * screen size) so the clamp matches the PC. Output stays in normalized coords,
 * so it is resolution independent and stays smooth at any zoom level.
 */
const SMOOTH_MIN_DIST = 0.75;
const SMOOTH_STEP = 1.5;
const SMOOTH_MIN_SIGMA = 5;
const SMOOTH_MAX_SIGMA = 12;
const SMOOTH_MAX_PASSES = 200;

function smoothStrokePts(pts, penSize) {
  const count = Math.floor(pts.length / 3);
  if (count < 4) return pts;
  const sx = screenW || 1920;
  const sy = screenH || 1080;

  // 1. Dedupe near-identical samples (in DIPs).
  const rx = [], ry = [], rp = [];
  for (let i = 0; i < count; i++) {
    const x = pts[i * 3] * sx;
    const y = pts[i * 3 + 1] * sy;
    const p = pts[i * 3 + 2] ?? 1;
    const k = rx.length - 1;
    if (k < 0 || Math.hypot(x - rx[k], y - ry[k]) >= SMOOTH_MIN_DIST) {
      rx.push(x); ry.push(y); rp.push(p);
    } else {
      rp[k] = p;
    }
  }
  if (rx.length < 4) return pts;
  // Always end exactly where the pen lifted.
  rx[rx.length - 1] = pts[(count - 1) * 3] * sx;
  ry[ry.length - 1] = pts[(count - 1) * 3 + 1] * sy;
  rp[rp.length - 1] = pts[(count - 1) * 3 + 2] ?? 1;

  const m = rx.length;

  // 2+3. Centripetal Catmull-Rom (Barry-Goldman), resampled by arc length.
  const ax = [2 * rx[0] - rx[1], ...rx, 2 * rx[m - 1] - rx[m - 2]];
  const ay = [2 * ry[0] - ry[1], ...ry, 2 * ry[m - 1] - ry[m - 2]];
  const dx = [rx[0]], dy = [ry[0]], dp = [rp[0]];

  for (let i = 1; i < ax.length - 2; i++) {
    const d01 = Math.hypot(ax[i] - ax[i - 1], ay[i] - ay[i - 1]);
    const d12 = Math.hypot(ax[i + 1] - ax[i], ay[i + 1] - ay[i]);
    const d23 = Math.hypot(ax[i + 2] - ax[i + 1], ay[i + 2] - ay[i + 1]);
    if (d12 < 1e-9) continue;
    const t0 = 0;
    const t1 = t0 + Math.max(Math.sqrt(d01), 1e-6);
    const t2 = t1 + Math.sqrt(d12);
    const t3 = t2 + Math.max(Math.sqrt(d23), 1e-6);
    const steps = Math.max(1, Math.ceil(d12 / SMOOTH_STEP));
    const p1 = rp[i - 1];
    const p2 = rp[i];
    for (let s = 1; s <= steps; s++) {
      const t = t1 + (t2 - t1) * s / steps;
      const l = (a, b, ta, tb) => a + (b - a) * ((t - ta) / (tb - ta));
      const a1x = l(ax[i - 1], ax[i], t0, t1), a1y = l(ay[i - 1], ay[i], t0, t1);
      const a2x = l(ax[i], ax[i + 1], t1, t2), a2y = l(ay[i], ay[i + 1], t1, t2);
      const a3x = l(ax[i + 1], ax[i + 2], t2, t3), a3y = l(ay[i + 1], ay[i + 2], t2, t3);
      const b1x = l(a1x, a2x, t0, t2), b1y = l(a1y, a2y, t0, t2);
      const b2x = l(a2x, a3x, t1, t3), b2y = l(a2y, a3y, t1, t3);
      dx.push(l(b1x, b2x, t1, t2));
      dy.push(l(b1y, b2y, t1, t2));
      dp.push(p1 + (p2 - p1) * s / steps);
    }
  }

  const n = dx.length;
  if (n < 4) return pts;

  // 4. Low-pass; blur radius grows with raw sample spacing.
  const gaps = [];
  for (let i = 0; i < m - 1; i++) gaps.push(Math.hypot(rx[i + 1] - rx[i], ry[i + 1] - ry[i]));
  gaps.sort((a, b) => a - b);
  const median = gaps[Math.floor(gaps.length / 2)] || 0;
  const sigma = Math.min(SMOOTH_MAX_SIGMA, Math.max(SMOOTH_MIN_SIGMA, median * 0.6));
  const passes = Math.min(SMOOTH_MAX_PASSES, Math.ceil(2 * sigma * sigma / (SMOOTH_STEP * SMOOTH_STEP)));

  let x = Float64Array.from(dx), y = Float64Array.from(dy), pr = Float64Array.from(dp);
  let tx = new Float64Array(n), ty = new Float64Array(n), tp = new Float64Array(n);
  for (let pass = 0; pass < passes; pass++) {
    tx[0] = x[0]; ty[0] = y[0]; tp[0] = pr[0];
    tx[n - 1] = x[n - 1]; ty[n - 1] = y[n - 1]; tp[n - 1] = pr[n - 1];
    for (let i = 1; i < n - 1; i++) {
      tx[i] = 0.25 * x[i - 1] + 0.5 * x[i] + 0.25 * x[i + 1];
      ty[i] = 0.25 * y[i - 1] + 0.5 * y[i] + 0.25 * y[i + 1];
      tp[i] = 0.25 * pr[i - 1] + 0.5 * pr[i] + 0.25 * pr[i + 1];
    }
    [x, tx] = [tx, x];
    [y, ty] = [ty, y];
    [pr, tp] = [tp, pr];
  }

  // 5. Clamp drift from the raw path, then back to normalized coordinates.
  const maxDev = Math.min(3, Math.max(1.5, (penSize || 4) * 0.6));
  const out = new Array(n * 3);
  for (let i = 0; i < n; i++) {
    let ox = x[i] - dx[i];
    let oy = y[i] - dy[i];
    const d = Math.hypot(ox, oy);
    if (d > maxDev) {
      ox *= maxDev / d;
      oy *= maxDev / d;
    }
    out[i * 3] = (dx[i] + ox) / sx;
    out[i * 3 + 1] = (dy[i] + oy) / sy;
    out[i * 3 + 2] = Math.min(1, Math.max(0, pr[i]));
  }
  return out;
}

/** Cached per stroke; recomputed only when new points arrive or the screen size changes. */
function getSmoothedPts(stroke) {
  // Pieces from erasing / strokes restored by the PC are already smoothed and dense.
  if (stroke.presmoothed) return stroke.pts;
  const key = `${stroke.pts.length}|${screenW}|${screenH}`;
  if (stroke._smoothKey !== key) {
    stroke._smoothKey = key;
    stroke._smooth = smoothStrokePts(stroke.pts, stroke.size);
  }
  return stroke._smooth;
}

function drawStroke(ctx, stroke) {
  if (stroke.pts.length < 3) return;
  const pts = getSmoothedPts(stroke);
  ctx.save();
  applyViewTransform(ctx);
  ctx.strokeStyle = normalizeColorHex(stroke.color);
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';

  updateContentRect();
  // Pen size is in PC DIPs. Map it into the same space the frame is drawn in (inside the view transform), so the
  // vector ink scales with zoom exactly like the ink baked into the streamed frame.
  const base = stroke.size * contentRect.w / (screenW || 1920);
  // Same pressure curve WPF uses for pen width on the PC: 0.5 pressure = pen size, 1.0 = 1.75x, 0.1 = 0.4x.
  // usePressure is stamped when the stroke is created; strokes without it follow the current setting.
  const usePressure = stroke.usePressure ?? pressureSensitive;
  const widthAt = i => Math.max(0.5, base * (0.25 + 1.5 * (usePressure ? (pts[i + 2] ?? 0.5) : 0.5)));
  // Group consecutive segments of (nearly) equal width into one path so pressure variation is kept
  // without issuing one stroke() call per segment.
  updateContentRect();
  const cr = contentRect;
  let px = cr.x + pts[0] * cr.w;
  let py = cr.y + pts[1] * cr.h;
  let curW = Math.round(widthAt(0) * 4) / 4;
  ctx.lineWidth = curW;
  ctx.beginPath();
  ctx.moveTo(px, py);
  for (let i = 3; i < pts.length; i += 3) {
    const x = cr.x + pts[i] * cr.w;
    const y = cr.y + pts[i + 1] * cr.h;
    const w = Math.round(widthAt(i) * 4) / 4;
    if (w !== curW) {
      ctx.stroke();
      curW = w;
      ctx.lineWidth = curW;
      ctx.beginPath();
      ctx.moveTo(px, py);
    }
    ctx.lineTo(x, y);
    px = x; py = y;
  }
  ctx.stroke();
  ctx.restore();
}

function drawShape(ctx, s) {
  updateContentRect();
  const cr = contentRect;
  const x1 = cr.x + s.x1 * cr.w;
  const y1 = cr.y + s.y1 * cr.h;
  const x2 = cr.x + s.x2 * cr.w;
  const y2 = cr.y + s.y2 * cr.h;
  const lw = Math.max(0.5, s.size * cr.w / (screenW || 1920));

  ctx.save();
  applyViewTransform(ctx);
  ctx.strokeStyle = normalizeColorHex(s.color);
  ctx.lineWidth = lw;
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  ctx.beginPath();
  if (s.tool === 'Rectangle') {
    ctx.rect(Math.min(x1, x2), Math.min(y1, y2), Math.abs(x2 - x1), Math.abs(y2 - y1));
  } else if (s.tool === 'Ellipse') {
    ctx.ellipse((x1 + x2) / 2, (y1 + y2) / 2, Math.abs(x2 - x1) / 2, Math.abs(y2 - y1) / 2, 0, 0, Math.PI * 2);
  } else {
    ctx.moveTo(x1, y1);
    ctx.lineTo(x2, y2);
    if (s.tool === 'Arrow') {
      const ang = Math.atan2(y2 - y1, x2 - x1);
      // Same head size as the PC (ShapeDrawingHelper.AddArrowHead), converted from DIPs.
      const head = Math.max(8, s.size * 3) * cr.w / (screenW || 1920);
      for (const side of [-1, 1]) {
        ctx.moveTo(x2, y2);
        ctx.lineTo(x2 - head * Math.cos(ang + side * 0.5), y2 - head * Math.sin(ang + side * 0.5));
      }
    }
  }
  ctx.stroke();
  ctx.restore();
}

function drawEraserCursor(ctx) {
  if (!eraserCursor) return;
  updateContentRect();
  const cr = contentRect;
  const x = cr.x + eraserCursor.nx * cr.w;
  const y = cr.y + eraserCursor.ny * cr.h;
  const radius = (eraserDiameterDips() / 2) * cr.w / (screenW || 1920);
  ctx.save();
  applyViewTransform(ctx);
  ctx.beginPath();
  ctx.arc(x, y, radius, 0, Math.PI * 2);
  ctx.fillStyle = 'rgba(255,255,255,0.18)';
  ctx.fill();
  ctx.lineWidth = 1.5 / Math.max(0.25, view.scale);
  ctx.strokeStyle = 'rgba(30,30,30,0.9)';
  ctx.stroke();
  ctx.restore();
}

/** Must match OverlayWindow.OnRemoteErase on the PC. */
function eraserDiameterDips() {
  return Math.max(12, size * 2);
}

/**
 * Instant local feedback for the eraser: drop our own vector strokes/shapes under the eraser. The PC applies the
 * same erase authoritatively (and tells us via strokesRemoved), and ink drawn on the PC itself only disappears
 * when the next streamed frame arrives.
 */
function eraseLocalAt(ax, ay, bx, by, whole) {
  const r = eraserDiameterDips() / 2;
  const sx = screenW || 1920;
  const sy = screenH || 1080;
  // Eraser segment in DIPs.
  const pax = ax * sx, pay = ay * sy, pbx = bx * sx, pby = by * sy;
  let changed = false;

  for (const [id, st] of [...vectorInk]) {
    const pts = getSmoothedPts(st);
    const rr = r + (st.size || 4) / 2;
    const n = Math.floor(pts.length / 3);
    const hit = new Array(n);
    let any = false;
    for (let i = 0; i < n; i++) {
      hit[i] = distToSegment(pts[i * 3] * sx, pts[i * 3 + 1] * sy, pax, pay, pbx, pby) <= rr;
      if (hit[i]) any = true;
    }
    if (!any) continue;
    changed = true;
    vectorInk.delete(id);
    if (whole) continue;

    // Partial erase: keep the runs of points the eraser did not touch as temporary pieces. The PC does the same
    // authoritatively and replaces them via strokesRemoved + strokesAdded.
    const root = st.tmpOf || id;
    const minLen = (st.size || 4) * 0.75;
    let run = [];
    const flush = () => {
      if (run.length >= 2) {
        let len = 0;
        for (let k = 1; k < run.length; k++) {
          len += Math.hypot((pts[run[k] * 3] - pts[run[k - 1] * 3]) * sx, (pts[run[k] * 3 + 1] - pts[run[k - 1] * 3 + 1]) * sy);
        }
        if (len >= minLen) {
          const piece = [];
          for (const idx of run) piece.push(pts[idx * 3], pts[idx * 3 + 1], pts[idx * 3 + 2]);
          vectorInk.set('tmp:' + (++localPieceSeq), {
            color: st.color, size: st.size, pts: piece, presmoothed: true, tmpOf: root, usePressure: st.usePressure
          });
        }
      }
      run = [];
    };
    for (let i = 0; i < n; i++) {
      if (hit[i]) flush(); else run.push(i);
    }
    flush();
  }

  // Shapes are always erased whole. Test the eraser segment at roughly one-radius steps.
  const steps = Math.max(1, Math.ceil(Math.hypot(pbx - pax, pby - pay) / Math.max(1, r)));
  for (const [id, sh] of [...localShapes]) {
    const reach = r + (sh.size || 4) / 2;
    for (let i = 0; i <= steps; i++) {
      const t = i / steps;
      if (shapeOutlineDistance(sh, pax + (pbx - pax) * t, pay + (pby - pay) * t, sx, sy) <= reach) {
        localShapes.delete(id);
        changed = true;
        break;
      }
    }
  }
  return changed;
}

function distToSegment(px, py, ax, ay, bx, by) {
  const dx = bx - ax;
  const dy = by - ay;
  const len2 = dx * dx + dy * dy;
  const t = len2 < 1e-9 ? 0 : Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / len2));
  return Math.hypot(px - (ax + dx * t), py - (ay + dy * t));
}

/** Distance (DIPs) from a point to the outline of a shape, mirroring ShapeHitTester on the PC. */
function shapeOutlineDistance(sh, px, py, sx, sy) {
  const x1 = sh.x1 * sx, y1 = sh.y1 * sy, x2 = sh.x2 * sx, y2 = sh.y2 * sy;
  if (sh.tool === 'Rectangle') {
    const l = Math.min(x1, x2), r = Math.max(x1, x2), t = Math.min(y1, y2), b = Math.max(y1, y2);
    return Math.min(
      distToSegment(px, py, l, t, r, t), distToSegment(px, py, r, t, r, b),
      distToSegment(px, py, r, b, l, b), distToSegment(px, py, l, b, l, t));
  }
  if (sh.tool === 'Ellipse') {
    const cx = (x1 + x2) / 2, cy = (y1 + y2) / 2;
    const rx = Math.abs(x2 - x1) / 2, ry = Math.abs(y2 - y1) / 2;
    let best = Infinity;
    let prevX = cx + rx, prevY = cy;
    for (let i = 1; i <= 48; i++) {
      const a = 2 * Math.PI * i / 48;
      const nx = cx + Math.cos(a) * rx, ny = cy + Math.sin(a) * ry;
      best = Math.min(best, distToSegment(px, py, prevX, prevY, nx, ny));
      prevX = nx; prevY = ny;
    }
    return best;
  }
  // Line / Arrow
  return distToSegment(px, py, x1, y1, x2, y2);
}

function pruneLocalShapes(frameRev) {
  const now = Date.now();
  for (const [id, sh] of localShapes) {
    const shown = sh.ackRev !== null && frameRev >= sh.ackRev && now - sh.ackAt >= LOCAL_SHAPE_RENDER_GRACE_MS;
    if (shown || now - sh.since > LOCAL_SHAPE_TIMEOUT_MS) {
      localShapes.delete(id);
    }
  }
}

function setPeerShape(msg, committed) {
  peerShapes.set(msg.id, {
    tool: msg.tool, x1: msg.x1, y1: msg.y1, x2: msg.x2, y2: msg.y2, color: msg.color, size: msg.size
  });
  if (committed) {
    setTimeout(() => { peerShapes.delete(msg.id); redraw(); }, 1500);
  }
  redraw();
}

function startPeerStroke(msg) {
  peerStrokes.set(msg.id, {
    color: normalizeColorHex(msg.color), size: msg.size, pts: [msg.x, msg.y, msg.p ?? 1], usePressure: pressureSensitive
  });
  redraw();
}

function appendPeer(id, pts) {
  const s = peerStrokes.get(id);
  if (!s) return;
  s.pts.push(...pts);
  redraw();
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
  if (target.closest('.ui')) return false;
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
    const now = Date.now();
    const isDouble = lastEraseTap
      && now - lastEraseTap.t < DOUBLE_TAP_MS
      && Math.hypot(clientX - lastEraseTap.cx, clientY - lastEraseTap.cy) < DOUBLE_TAP_SLOP_PX;
    if (isDouble) {
      // Double tap (and optionally keep dragging): erase whole strokes under the eraser.
      clearTimeout(lastEraseTap.timer);
      lastEraseTap = null;
      eraseGesture = { whole: true, started: true, cx: clientX, cy: clientY, nx, ny, prevNx: nx, prevNy: ny };
      eraseSegment(eraseGesture, nx, ny, nx, ny);
    } else {
      // Hold off: a press that never moves might be the first tap of a double tap, so don't cut anything yet.
      eraseGesture = { whole: false, started: false, cx: clientX, cy: clientY, nx, ny, prevNx: nx, prevNy: ny };
    }
    eraserCursor = { nx, ny };
    redraw();
    return;
  }
  drawPointerErasing = false;

  if (['Line', 'Arrow', 'Rectangle', 'Ellipse'].includes(tool)) {
    shapeDrag = { id, x1: nx, y1: ny, x2: nx, y2: ny };
    return;
  }

  activeLocal.set(id, { color: normalizeColorHex(color), size, pts: [nx, ny, p], usePressure: pressureSensitive });
  frameCanvas._activeStrokeId = id;
  log(`draw begin ${tool} ${source} (${nx.toFixed(3)},${ny.toFixed(3)})`);
  send({ t: 'strokeBegin', id, color, size, x: nx, y: ny, p });
}

function continueDrawAtClient(clientX, clientY, p) {
  if (shapeDrag) {
    const [nx, ny] = normFromClient(clientX, clientY);
    shapeDrag.x2 = nx;
    shapeDrag.y2 = ny;
    // Preview is drawn locally only. Sending shapePreview would make the PC draw it too, and the streamed
    // frame (low fps) would show a laggy ghost behind the local one. The PC only gets the shape on commit.
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
    const g = eraseGesture;
    if (g) {
      if (!g.started && Math.hypot(clientX - g.cx, clientY - g.cy) > ERASE_DRAG_SLOP_PX) {
        // It's a drag, not a tap: start cutting from where the press began.
        g.started = true;
      }
      if (g.started) {
        eraseSegment(g, g.prevNx, g.prevNy, nx, ny);
        g.prevNx = nx;
        g.prevNy = ny;
      }
    }
    eraserCursor = { nx, ny };
  }
  redraw();
}

function continueDraw(e) {
  const events = e.getCoalescedEvents ? e.getCoalescedEvents() : [e];
  for (const ev of events) {
    continueDrawAtClient(ev.clientX, ev.clientY, pressureFromPointer(ev));
  }
}

/** Sends an erase (a segment from a to b, normalized) and mirrors it locally for instant feedback. */
function eraseSegment(gesture, ax, ay, bx, by) {
  const pts = (ax === bx && ay === by) ? [ax, ay] : [ax, ay, bx, by];
  send({ t: 'erase', pts, size, whole: gesture.whole === true });
  eraseLocalAt(ax, ay, bx, by, gesture.whole === true);
}

function endDraw() {
  if (eraserCursor) {
    eraserCursor = null;
    redraw();
  }
  if (eraseGesture) {
    const g = eraseGesture;
    eraseGesture = null;
    if (!g.started && !g.whole) {
      // A tap. Wait briefly for a second tap (= erase whole stroke); otherwise it's a small partial erase.
      const tap = { t: Date.now(), cx: g.cx, cy: g.cy, nx: g.nx, ny: g.ny, timer: 0 };
      tap.timer = setTimeout(() => {
        if (lastEraseTap === tap) lastEraseTap = null;
        eraseSegment({ whole: false }, tap.nx, tap.ny, tap.nx, tap.ny);
        redraw();
      }, DOUBLE_TAP_MS);
      lastEraseTap = tap;
    }
  }
  if (shapeDrag) {
    const s = shapeDrag;
    send({
      t: 'shapeCommit', id: s.id, tool,
      x1: s.x1, y1: s.y1, x2: s.x2, y2: s.y2, color, size
    });
    // Keep showing it locally until the PC's streamed frame contains it.
    localShapes.set(s.id, {
      tool, x1: s.x1, y1: s.y1, x2: s.x2, y2: s.y2, color, size,
      ackRev: null, ackAt: 0, since: Date.now()
    });
    setTimeout(() => { pruneLocalShapes(inkRevision); redraw(); }, LOCAL_SHAPE_TIMEOUT_MS + 100);
    shapeDrag = null;
    redraw();
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
      if (activeLocal.has(id)) {
        log(`ack timeout id=${id.slice(0, 8)}… (kept vector ink)`);
        commitLocalStrokeToVector(id);
        redraw();
      }
    }, 8000);
  }
  redraw();
}

function pointerDown(e) {
  if (!sessionReady || isUiTarget(e.target)) return;
  if (!isDrawAreaTarget(e.target)) return;
  // First tap outside an open popover only dismisses it (no accidental stroke).
  if (openPopover) {
    closePopover();
    return;
  }
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
  if (openPopover && !isUiTarget(e.target)) {
    closePopover();
    e.stopImmediatePropagation();
    return;
  }
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
  if (openPopover) {
    closePopover();
    e.preventDefault();
    return;
  }
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

const TOOL_DEFS = [
  { name: 'Pen', icon: 'pen', label: 'Pen' },
  { name: 'Line', icon: 'line', label: 'Line' },
  { name: 'Arrow', icon: 'arrow', label: 'Arrow' },
  { name: 'Rectangle', icon: 'rectangle', label: 'Rectangle' },
  { name: 'Ellipse', icon: 'ellipse', label: 'Ellipse' },
  { name: 'Eraser', icon: 'eraser', label: 'Eraser' }
];

const toolButtons = new Map();
const swatchButtons = new Map();
let sizeDragging = false;
let sizeSendTimer = null;
let clearConfirmTimer = null;

function isLightColor(hex) {
  const m = /^#([0-9A-F]{2})([0-9A-F]{2})([0-9A-F]{2})$/i.exec(hex || '');
  if (!m) return false;
  const lum = 0.299 * parseInt(m[1], 16) + 0.587 * parseInt(m[2], 16) + 0.114 * parseInt(m[3], 16);
  return lum > 170;
}

function selectTool(name) {
  tool = name;
  if (!followHost) send({ t: 'setTool', tool: name });
  updateUi();
}

function selectColor(hex) {
  color = normalizeColorHex(hex);
  if (!followHost) send({ t: 'setColor', color });
  updateUi();
  closePopover();
}

function sendSizeThrottled() {
  if (sizeSendTimer) return;
  sizeSendTimer = setTimeout(() => {
    sizeSendTimer = null;
    if (!followHost) send({ t: 'setSize', size });
  }, 60);
}

function resetClearConfirm() {
  clearTimeout(clearConfirmTimer);
  clearConfirmTimer = null;
  clearBtn.classList.remove('confirm');
  clearBtn.innerHTML = icon('clear', 22);
}

function setDockEdge(edge, { persist = true, animate = false } = {}) {
  if (!DOCK_EDGES.includes(edge)) edge = 'bottom';
  dockEdge = edge;
  hudEl.dataset.edge = edge;
  dockGrip.innerHTML = icon(edge === 'bottom' ? 'gripVertical' : 'gripHorizontal', 20);
  if (persist) localStorage.setItem('overink.dockEdge', edge);
  if (animate) {
    dockEl.classList.remove('snapped');
    void dockEl.offsetWidth; // restart the animation
    dockEl.classList.add('snapped');
  }
  if (openPopover) positionPopover(openPopover);
}

/** Which edge the dock should snap to for a pointer position: outer 30% sides, otherwise the bottom. */
function edgeFromPoint(x, y) {
  if (x < innerWidth * 0.3) return 'left';
  if (x > innerWidth * 0.7) return 'right';
  return 'bottom';
}

/** Drag the grip to move the dock; it snaps to the nearest edge on release. */
function initDockDrag() {
  const DRAG_THRESHOLD = 6;
  let drag = null;

  const showTarget = (edge) => {
    for (const zone of snapZonesEl.children) {
      zone.classList.toggle('active', zone.dataset.edge === edge);
    }
  };

  dockGrip.addEventListener('pointerdown', (e) => {
    if (drag || (e.pointerType === 'mouse' && e.button !== 0)) return;
    e.preventDefault();
    e.stopPropagation();
    closePopover();
    drag = { id: e.pointerId, sx: e.clientX, sy: e.clientY, moved: false, target: dockEdge };
    try { dockGrip.setPointerCapture(e.pointerId); } catch { /* ignore */ }
  });

  dockGrip.addEventListener('pointermove', (e) => {
    if (!drag || e.pointerId !== drag.id) return;
    e.preventDefault();
    const dx = e.clientX - drag.sx;
    const dy = e.clientY - drag.sy;
    if (!drag.moved) {
      if (Math.hypot(dx, dy) < DRAG_THRESHOLD) return;
      drag.moved = true;
      dockEl.classList.add('dragging');
      snapZonesEl.classList.remove('panel-hidden');
    }
    dockEl.style.transform = `translate(${dx}px, ${dy}px)`;
    drag.target = edgeFromPoint(e.clientX, e.clientY);
    showTarget(drag.target);
  });

  const finish = (e, cancelled) => {
    if (!drag || e.pointerId !== drag.id) return;
    const { moved, target } = drag;
    drag = null;
    try { dockGrip.releasePointerCapture(e.pointerId); } catch { /* ignore */ }
    if (!moved) return;
    dockEl.classList.remove('dragging');
    dockEl.style.transform = '';
    snapZonesEl.classList.add('panel-hidden');
    showTarget(null);
    if (!cancelled) setDockEdge(target, { animate: true });
  };
  dockGrip.addEventListener('pointerup', (e) => finish(e, false));
  dockGrip.addEventListener('pointercancel', (e) => finish(e, true));
  dockEl.addEventListener('animationend', () => dockEl.classList.remove('snapped'));
  // Tapping the grip without dragging must not open the iOS callout / select text.
  dockGrip.addEventListener('contextmenu', (e) => e.preventDefault());
}

/** Build the dock once; updateUi() then only toggles state so open popovers/sliders are never rebuilt. */
function buildDock() {
  toolsEl.innerHTML = '';
  for (const def of TOOL_DEFS) {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'dock-btn';
    b.title = def.label;
    b.setAttribute('aria-label', def.label);
    b.innerHTML = icon(def.icon, 22);
    b.onclick = () => { closePopover(); selectTool(def.name); };
    toolsEl.appendChild(b);
    toolButtons.set(def.name, b);
  }

  swatchesEl.innerHTML = '';
  for (const c of colors) {
    const s = document.createElement('button');
    s.type = 'button';
    s.className = 'sw';
    s.style.background = c;
    s.setAttribute('aria-label', `Color ${c}`);
    s.innerHTML = icon('check', 18);
    s.firstElementChild.style.opacity = '0';
    s.firstElementChild.style.color = isLightColor(c) ? '#111' : '#fff';
    s.onclick = () => selectColor(c);
    swatchesEl.appendChild(s);
    swatchButtons.set(c, s);
  }

  undoBtn.innerHTML = icon('undo', 22);
  redoBtn.innerHTML = icon('redo', 22);
  settingsBtn.innerHTML = icon('settings', 22);
  resetClearConfirm();
  undoBtn.onclick = () => send({ t: 'undo' });
  redoBtn.onclick = () => send({ t: 'redo' });
  clearBtn.onclick = () => {
    if (clearBtn.classList.contains('confirm')) {
      send({ t: 'clear' });
      clearVectorInkLayers();
      redraw();
      resetClearConfirm();
      return;
    }
    closePopover();
    clearBtn.classList.add('confirm');
    clearBtn.innerHTML = icon('clear', 22);
    clearConfirmTimer = setTimeout(resetClearConfirm, 3000);
  };

  colorBtn.onclick = () => setPopover('color');
  sizeBtn.onclick = () => setPopover('size');

  pressureToggle.addEventListener('change', () => {
    pressureSensitive = pressureToggle.checked;
    send({ t: 'setPressure', enabled: pressureSensitive });
    redraw();
  });

  sizeSlider.addEventListener('pointerdown', () => { sizeDragging = true; });
  const endSizeDrag = () => { sizeDragging = false; if (!followHost) send({ t: 'setSize', size }); };
  sizeSlider.addEventListener('pointerup', endSizeDrag);
  sizeSlider.addEventListener('pointercancel', endSizeDrag);
  sizeSlider.addEventListener('input', () => {
    size = Number(sizeSlider.value);
    updateUi();
    sendSizeThrottled();
  });

  document.getElementById('bugIcon').innerHTML = icon('bug', 20);
  document.getElementById('resetIcon').innerHTML = icon('reset', 20);
  document.getElementById('hintPen').innerHTML = icon('pen', 18);
  document.getElementById('hintHand').innerHTML = icon('hand', 18);
  document.getElementById('hintZoom').innerHTML = icon('zoom', 18);
  setStatus(statusEl.textContent, 'connecting');
}

function updateUi() {
  for (const [name, b] of toolButtons) {
    const active = tool === name;
    b.classList.toggle('active', active);
    b.setAttribute('aria-pressed', active ? 'true' : 'false');
  }
  const current = normalizeColorHex(color);
  colorDot.style.background = current;
  for (const [c, s] of swatchButtons) {
    const selected = normalizeColorHex(c) === current;
    s.classList.toggle('selected', selected);
    s.firstElementChild.style.opacity = selected ? '1' : '0';
  }
  const clamped = Math.max(1, Math.min(20, Number(size) || 4));
  if (Number(sizeSlider.value) !== clamped) sizeSlider.value = String(clamped);
  sizeValue.textContent = String(Math.round(clamped));
  if (pressureToggle.checked !== pressureSensitive) pressureToggle.checked = pressureSensitive;
  // The curve's thickness tracks the setting. Sizes 1-20 map to a 1.25-8 unit stroke
  // (viewBox units, not literal pixels) so the thickest line still fits inside the icon.
  const t = (clamped - 1) / 19;
  const strokeUnits = (1.25 + t * 6.75).toFixed(2);
  sizeDot.setAttribute('stroke-width', strokeUnits);
  sizePreview.setAttribute('stroke-width', strokeUnits);
  sizePreview.setAttribute('stroke', current);
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

buildDock();
setDockEdge(dockEdge, { persist: false });
initDockDrag();
updateUi();
initSettingsUi();
log(`page v11`, { alwaysShow: debugLog });
connect();
