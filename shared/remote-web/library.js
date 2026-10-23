import { icon } from './icons.js?v=6';
// Module URLs are matched exactly by the browser cache: keep this ?v= on icons.js identical to the one in app.js.

/** HTTP failure from the PC's /lib endpoints. `status` is the HTTP status, `body` the parsed JSON error if any. */
export class LibraryError extends Error {
  constructor(status, body) {
    super(`Library request failed (${status})`);
    this.status = status;
    this.body = body;
  }
}

function decodeBlob(blob) {
  if (typeof createImageBitmap === 'function') {
    return createImageBitmap(blob).catch(() => decodeViaImage(blob));
  }
  return decodeViaImage(blob);
}

function decodeViaImage(blob) {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(blob);
    const img = new Image();
    img.onload = () => { URL.revokeObjectURL(url); resolve(img); };
    img.onerror = () => { URL.revokeObjectURL(url); reject(new Error('Image decode failed')); };
    img.src = url;
  });
}

/**
 * Client for the capture library endpoints. Library data goes over HTTP (not the WebSocket) because images
 * are far larger than a WebSocket message may be. `getToken` returns the capability token the PC issued in
 * `welcome`; it changes on every reconnect, so it is read on each call.
 */
export function createLibraryApi(getToken) {
  const enc = encodeURIComponent;

  async function request(path, init) {
    const token = getToken();
    if (!token) throw new LibraryError(401, null);
    const sep = path.includes('?') ? '&' : '?';
    const res = await fetch(`${path}${sep}k=${enc(token)}`, init);
    if (!res.ok) {
      let body = null;
      try { body = await res.json(); } catch { /* not JSON */ }
      throw new LibraryError(res.status, body);
    }
    return res;
  }

  return {
    async list() {
      return (await request('/lib')).json();
    },
    thumbUrl(item) {
      return `/lib/${enc(item.id)}/thumb?k=${enc(getToken())}&v=${enc(item.rev)}`;
    },
    async image(id) {
      const blob = await (await request(`/lib/${enc(id)}/original`)).blob();
      return decodeBlob(blob);
    },
    async strokes(id) {
      return (await request(`/lib/${enc(id)}/strokes`)).json();
    },
    /** `strokesJson` is the strokes array already serialized (cached per stroke by the caller), so a save of a big
     *  capture doesn't re-stringify every point on the main thread while the pen is down. */
    async save(id, baseRev, strokesJson, veil = 0) {
      const res = await request(`/lib/${enc(id)}/strokes`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: `{"baseRev":${JSON.stringify(baseRev)},"veil":${Number(veil) || 0},"strokes":${strokesJson}}`
      });
      return res.json();
    },
    async remove(id) {
      await request(`/lib/${enc(id)}`, { method: 'DELETE' });
    },
    /** Copies a capture on the PC; resolves with the new library entry. */
    async duplicate(id) {
      return (await request(`/lib/${enc(id)}/duplicate`, { method: 'POST' })).json();
    }
  };
}

const LONG_PRESS_MS = 500;
const LONG_PRESS_SLOP_PX = 10;

let cardMenu = null;

function closeCardMenu() {
  if (!cardMenu) return;
  cardMenu.card.classList.remove('menu-open');
  cardMenu.layer.remove();
  document.removeEventListener('keydown', cardMenu.onKey, true);
  cardMenu = null;
}

/** Floating menu for one capture, placed beside the press point and kept on screen. */
function showCardMenu(card, item, x, y, handlers) {
  closeCardMenu();

  const layer = document.createElement('div');
  layer.className = 'ui lib-menu-layer';
  layer.addEventListener('pointerdown', (e) => {
    if (e.target === layer) { e.preventDefault(); closeCardMenu(); }
  });
  layer.addEventListener('contextmenu', (e) => { e.preventDefault(); if (e.target === layer) closeCardMenu(); });

  const menu = document.createElement('div');
  menu.className = 'lib-menu';
  menu.setAttribute('role', 'menu');

  const title = document.createElement('div');
  title.className = 'lib-menu-title';
  title.textContent = item.name;
  menu.appendChild(title);

  const duplicate = document.createElement('button');
  duplicate.type = 'button';
  duplicate.className = 'lib-menu-item';
  duplicate.setAttribute('role', 'menuitem');
  duplicate.innerHTML = `${icon('copy', 20)}<span>Duplicate</span>`;
  duplicate.addEventListener('click', async () => {
    closeCardMenu();
    card.classList.add('busy');
    try {
      await handlers.onDuplicate(item);
    } catch {
      // the handler already told the user
    } finally {
      card.classList.remove('busy');
    }
  });
  menu.appendChild(duplicate);

  layer.appendChild(menu);
  document.body.appendChild(layer);

  const onKey = (e) => { if (e.key === 'Escape') closeCardMenu(); };
  document.addEventListener('keydown', onKey, true);
  card.classList.add('menu-open');
  cardMenu = { layer, card, onKey };

  // Above the finger when there is room (it would be hidden under the hand otherwise), else below it.
  const margin = 10;
  const rect = menu.getBoundingClientRect();
  const left = Math.min(Math.max(x - rect.width / 2, margin), window.innerWidth - rect.width - margin);
  const above = y - rect.height - 18;
  const top = above >= margin ? above : Math.min(y + 18, window.innerHeight - rect.height - margin);
  menu.style.left = `${Math.round(left)}px`;
  menu.style.top = `${Math.round(Math.max(top, margin))}px`;
}

/**
 * Opens `showCardMenu` on a press-and-hold (or a right click) on the card. Returns a function that reports
 * (once) whether a hold just fired, so the tap that ends the hold can be ignored.
 */
function attachCardMenu(card, item, handlers) {
  let timer = null;
  let startX = 0;
  let startY = 0;
  let held = false;

  const cancel = () => {
    clearTimeout(timer);
    timer = null;
  };

  card.addEventListener('pointerdown', (e) => {
    held = false;
    cancel();
    if (e.button > 0 || e.target.closest('.lib-delete')) return;
    startX = e.clientX;
    startY = e.clientY;
    timer = setTimeout(() => {
      timer = null;
      held = true;
      showCardMenu(card, item, startX, startY, handlers);
    }, LONG_PRESS_MS);
  });
  card.addEventListener('pointermove', (e) => {
    if (timer && Math.hypot(e.clientX - startX, e.clientY - startY) > LONG_PRESS_SLOP_PX) cancel();
  });
  for (const type of ['pointerup', 'pointercancel', 'pointerleave']) card.addEventListener(type, cancel);

  // Mouse right click (and any browser that raises it for a long touch). Never let the native menu show.
  card.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    cancel();
    if (!cardMenu || cardMenu.card !== card) {
      held = true;
      showCardMenu(card, item, e.clientX, e.clientY, handlers);
    }
  });

  // The finger that opened the menu lifts over the card: don't also treat it as a tap that opens the capture.
  return () => {
    const wasHeld = held;
    held = false;
    return wasHeld;
  };
}

function formatTime(ms) {
  try {
    return new Date(ms).toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
  } catch {
    return '';
  }
}

/**
 * Renders the capture grid. `handlers.onOpen(item)` opens a capture; `handlers.onDelete(item)` deletes it
 * (called after the card's two-tap confirmation) and returns a promise; `handlers.onDuplicate(item)` copies it
 * (from the press-and-hold menu) and returns a promise.
 */
export function renderLibraryGrid(gridEl, items, api, handlers) {
  closeCardMenu();
  gridEl.textContent = '';
  for (const item of items) {
    const card = document.createElement('div');
    card.className = 'lib-card';

    const open = document.createElement('button');
    open.type = 'button';
    open.className = 'lib-open';
    open.setAttribute('aria-label', `Open ${item.name}`);

    const thumb = document.createElement('img');
    thumb.className = 'lib-thumb';
    thumb.loading = 'lazy';
    thumb.alt = '';
    thumb.draggable = false;
    thumb.src = api.thumbUrl(item);
    open.appendChild(thumb);

    const name = document.createElement('span');
    name.className = 'lib-name';
    name.textContent = item.name;
    open.appendChild(name);

    const time = document.createElement('span');
    time.className = 'lib-time';
    time.textContent = formatTime(item.createdAt);
    open.appendChild(time);

    const consumeHold = attachCardMenu(card, item, handlers);
    open.addEventListener('click', () => {
      if (consumeHold()) return;
      handlers.onOpen(item);
    });
    card.appendChild(open);

    const del = document.createElement('button');
    del.type = 'button';
    del.className = 'lib-delete';
    del.setAttribute('aria-label', `Delete ${item.name}`);
    del.innerHTML = icon('clear', 18);
    let confirmTimer = null;
    const resetConfirm = () => {
      clearTimeout(confirmTimer);
      confirmTimer = null;
      del.classList.remove('confirm');
      del.innerHTML = icon('clear', 18);
    };
    del.addEventListener('click', async (e) => {
      e.stopPropagation();
      if (!del.classList.contains('confirm')) {
        del.classList.add('confirm');
        del.textContent = 'Delete?';
        confirmTimer = setTimeout(resetConfirm, 3000);
        return;
      }
      resetConfirm();
      card.classList.add('busy');
      try {
        await handlers.onDelete(item);
      } catch {
        card.classList.remove('busy');
      }
    });
    card.appendChild(del);

    gridEl.appendChild(card);
  }
}
