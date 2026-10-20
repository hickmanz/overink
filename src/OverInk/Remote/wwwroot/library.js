import { icon } from './icons.js?v=4';
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
    }
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
 * (called after the card's two-tap confirmation) and returns a promise.
 */
export function renderLibraryGrid(gridEl, items, api, handlers) {
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

    open.addEventListener('click', () => handlers.onOpen(item));
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
