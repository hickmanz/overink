#!/usr/bin/env node
// Smoke test for the OverInk remote protocol against a running host (Windows or macOS).
//
//   node scripts/remote-smoke.mjs [host:port]
//
// Start the Mac app with `.build/debug/OverInkMac --remote` first. Connecting from the same machine is
// auto-paired. The test draws, erases, undoes and clears on the real overlay, so run it on a spare desktop.
// Requires Node 22+ (global WebSocket).

const target = process.argv[2] ?? "127.0.0.1:8787";
let failures = 0;

function check(name, ok, detail = "") {
  console.log(`${ok ? "ok  " : "FAIL"} ${name}${ok || !detail ? "" : ` (${detail})`}`);
  if (!ok) failures += 1;
}

class Client {
  constructor(name) {
    this.name = name;
    this.messages = [];
    this.frames = 0;
    this.waiters = [];
  }

  async connect() {
    this.ws = new WebSocket(`ws://${target}/ws`);
    this.ws.binaryType = "arraybuffer";
    this.ws.onmessage = (event) => {
      if (typeof event.data !== "string") {
        this.frames += 1;
        return;
      }
      const message = JSON.parse(event.data);
      this.messages.push(message);
      this.waiters = this.waiters.filter((waiter) => !waiter(message));
    };
    await new Promise((resolve, reject) => {
      this.ws.onopen = resolve;
      this.ws.onerror = () => reject(new Error("connect failed"));
    });
    this.send({ t: "hello", v: 1, deviceName: this.name });
    this.welcome = await this.next("welcome");
  }

  send(message) {
    this.ws.send(JSON.stringify(message));
  }

  next(type, predicate = () => true, timeout = 3000) {
    const existing = this.messages.find((m) => m.t === type && predicate(m));
    if (existing) {
      this.messages.splice(this.messages.indexOf(existing), 1);
      return Promise.resolve(existing);
    }
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`${this.name}: timed out waiting for ${type}`)), timeout);
      this.waiters.push((message) => {
        if (message.t !== type || !predicate(message)) return false;
        clearTimeout(timer);
        this.messages.splice(this.messages.indexOf(message), 1);
        resolve(message);
        return true;
      });
    });
  }

  async expectNone(type, ms = 400) {
    await new Promise((resolve) => setTimeout(resolve, ms));
    return !this.messages.some((m) => m.t === type);
  }

  close() {
    this.ws.close();
  }
}

const uuid = () => crypto.randomUUID();

try {
  const a = new Client("Smoke A");
  const b = new Client("Smoke B");
  await a.connect();
  await b.connect();

  check("welcome has client id", typeof a.welcome.clientId === "string" && a.welcome.clientId.length > 0);
  check("welcome has stream size", a.welcome.screenW > 0 && a.welcome.screenH > 0);
  check("loopback is trusted without storing a device key", a.welcome.deviceKey === undefined || a.welcome.deviceKey === null);
  check("welcome state has tool", typeof a.welcome.state.tool === "string");

  // Other iPads or browser tabs may be connected too, so look for our two by id.
  const ids = (m) => m.participants.map((p) => p.clientId);
  const roster = await b.next("roster", (m) => ids(m).includes(a.welcome.clientId) && ids(m).includes(b.welcome.clientId));
  check("roster lists both clients", ids(roster).includes(a.welcome.clientId) && ids(roster).includes(b.welcome.clientId));

  // A pen stroke: peers see it live, the author gets an ack.
  const stroke = uuid();
  a.send({ t: "strokeBegin", id: stroke, color: "#1E88E5", size: 6, x: 0.2, y: 0.2, p: 1 });
  const begin = await b.next("peerStrokeBegin", (m) => m.id === stroke);
  check("peer sees strokeBegin with author", begin.author === a.welcome.clientId);
  const pts = [];
  for (let i = 1; i <= 20; i += 1) pts.push(0.2 + i * 0.02, 0.2 + i * 0.01, 1);
  a.send({ t: "strokePoints", id: stroke, pts });
  const more = await b.next("peerStrokePoints", (m) => m.id === stroke);
  check("peer sees points", more.pts.length === pts.length);
  a.send({ t: "strokeEnd", id: stroke });
  const ack = await a.next("ack", (m) => m.id === stroke);
  check("author gets ack with ink revision", typeof ack.inkRevision === "number" && ack.inkRevision > 0);
  await b.next("peerStrokeEnd", (m) => m.id === stroke);
  check("author gets no peer echo", await a.expectNone("peerStrokeBegin"));

  // Shape commit.
  const shape = uuid();
  a.send({ t: "shapeCommit", id: shape, tool: "Rectangle", x1: 0.5, y1: 0.5, x2: 0.7, y2: 0.6, color: "#43A047", size: 4 });
  await a.next("ack", (m) => m.id === shape);
  await b.next("peerShapeCommit", (m) => m.id === shape);
  check("shape committed and relayed", true);

  // Erase across the pen stroke: everyone hears the authoritative result.
  a.send({ t: "erase", pts: [0.3, 0.25, 0.5, 0.35], size: 30, whole: true });
  const removed = await b.next("strokesRemoved", (m) => m.ids.includes(stroke));
  check("erase removes the stroke (ids are the client's GUID)", removed.ids.includes(stroke));
  await a.next("strokesRemoved", (m) => m.ids.includes(stroke));
  check("author also hears strokesRemoved", true);

  // Per-author undo restores it for everyone.
  b.send({ t: "undo" });
  check("undo by another author does not touch A's erase", await a.expectNone("strokesAdded"));
  a.send({ t: "undo" });
  const added = await b.next("strokesAdded", (m) => m.strokes.some((s) => s.id === stroke));
  check("author undo restores the erased stroke", added.strokes.length >= 1);

  // Tool and colour changes are broadcast as state.
  const original = a.welcome.state.color.toUpperCase();
  const probe = original === "#8E24AA" ? "#00ACC1" : "#8E24AA";
  a.send({ t: "setColor", color: probe });
  const state = await b.next("state", (m) => m.color.toUpperCase() === probe);
  check("setColor reaches other clients", state.color.toUpperCase() === probe);

  a.send({ t: "setColor", color: original }); // put the user's colour back

  a.send({ t: "ping", ms: 5 });
  const pong = await a.next("pong");
  check("ping gets pong", pong.t === "pong");

  a.send({ t: "hover", clientId: "spoofed", x: 0.4, y: 0.4 });
  const hover = await b.next("hover");
  check("hover is relayed with the real client id", hover.clientId === a.welcome.clientId);

  // A frame should be flowing when the streamer is running.
  await new Promise((resolve) => setTimeout(resolve, 1500));
  console.log(`info frames received by A: ${a.frames}`);

  a.send({ t: "clear" });
  await b.next("cleared");
  check("clear is broadcast", true);

  // Library over HTTP. Makes its own capture (needs an awake screen) and deletes everything it creates.
  const base = `http://${target}`;
  const key = a.welcome.libToken;
  check("loopback receives a library token", typeof key === "string" && key.length > 10);
  check("library rejects a missing token", (await fetch(`${base}/lib`)).status === 401);
  check("library rejects a wrong token", (await fetch(`${base}/lib?k=nope`)).status === 401);

  a.send({ t: "capture" });
  const outcome = await Promise.race([
    a.next("captured", () => true, 15000).then((m) => ({ ok: true, m })),
    a.next("captureFailed", () => true, 15000).then((m) => ({ ok: false, m })),
  ]).catch((error) => ({ ok: false, m: { message: error.message } }));

  if (!outcome.ok) {
    console.log(`skip library tests: capture failed (${outcome.m.message})`);
  } else {
    const id = outcome.m.id;
    const lib = (path, init) => fetch(`${base}${path}${path.includes("?") ? "&" : "?"}k=${encodeURIComponent(key)}`, init);
    const listing = await (await lib("/lib")).json();
    const item = listing.find((entry) => entry.id === id);
    check("captured item is listed", !!item && item.w > 0 && item.h > 0 && typeof item.rev === "string");
    check("capture made by the stream is editable", item?.editable === true);

    const thumb = await lib(`/lib/${encodeURIComponent(id)}/thumb`);
    check("thumbnail is a JPEG", thumb.status === 200 && thumb.headers.get("content-type") === "image/jpeg");
    const originalRes = await lib(`/lib/${encodeURIComponent(id)}/original`);
    const originalBytes = new Uint8Array(await originalRes.arrayBuffer());
    check("original is a PNG", originalRes.status === 200 && originalBytes[1] === 0x50 && originalBytes[2] === 0x4e);

    const doc = await (await lib(`/lib/${encodeURIComponent(id)}/strokes`)).json();
    check("strokes reply has revision and size", doc.rev === item.rev && doc.w === item.w && doc.h === item.h);

    const stroke = { color: "#1E88E5", size: 8, pts: [100, 100, 1, 220, 160, 1, 340, 120, 1], ignorePressure: false };
    const saved = await lib(`/lib/${encodeURIComponent(id)}/strokes`, {
      method: "POST",
      body: JSON.stringify({ baseRev: doc.rev, strokes: [...doc.strokes, stroke], veil: 0.2 }),
    });
    const savedBody = await saved.json();
    check("save with the current revision succeeds", saved.status === 200 && typeof savedBody.rev === "string" && savedBody.rev !== doc.rev);

    const again = await lib(`/lib/${encodeURIComponent(id)}/strokes`, {
      method: "POST",
      body: JSON.stringify({ baseRev: doc.rev, strokes: [stroke] }),
    });
    const conflict = await again.json();
    check("save with a stale revision is a 409 with the current revision", again.status === 409 && conflict.rev === savedBody.rev, `status ${again.status}, got ${JSON.stringify(conflict)}, saved ${savedBody.rev}`);

    const reread = await (await lib(`/lib/${encodeURIComponent(id)}/strokes`)).json();
    check("saved stroke and veil read back", reread.strokes.length === doc.strokes.length + 1 && Math.abs(reread.veil - 0.2) < 1e-6);
    const back = reread.strokes[reread.strokes.length - 1];
    check("stroke geometry survives the pixel/point conversion", Math.abs(back.pts[0] - 100) < 0.5 && Math.abs(back.pts[4] - 160) < 0.5 && Math.abs(back.size - 8) < 0.5);

    const bad = await lib(`/lib/${encodeURIComponent(id)}/strokes`, {
      method: "POST",
      body: JSON.stringify({ baseRev: savedBody.rev, strokes: [{ color: "#000000", size: 1, pts: [1, 2], ignorePressure: false }] }),
    });
    check("invalid points are a 400", bad.status === 400);

    const dup = await (await lib(`/lib/${encodeURIComponent(id)}/duplicate`, { method: "POST" })).json();
    check("duplicate returns a new item", typeof dup.id === "string" && dup.id !== id);

    const changed = a.next("libraryChanged", () => true, 4000).then(() => true, () => false);
    await lib(`/lib/${encodeURIComponent(dup.id)}`, { method: "DELETE" });
    await lib(`/lib/${encodeURIComponent(id)}`, { method: "DELETE" });
    check("library change is pushed to the iPad", await changed);
    const after = await (await lib("/lib")).json();
    check("deleted items are gone", !after.some((entry) => entry.id === id || entry.id === dup.id));
    check("unknown item is a 404", (await lib(`/lib/${encodeURIComponent(id)}/thumb`)).status === 404);
    check("path traversal in an id finds nothing", (await lib(`/lib/..%2F..%2Fetc%2Fpasswd/original`)).status === 404);
  }

  // Protocol hygiene.
  const bad = new Client("bad");
  bad.ws = new WebSocket(`ws://${target}/ws`);
  await new Promise((resolve) => (bad.ws.onopen = resolve));
  const closed = new Promise((resolve) => (bad.ws.onclose = resolve));
  bad.ws.send("not json");
  await Promise.race([closed, new Promise((resolve) => setTimeout(resolve, 2000))]);
  check("garbage before hello closes the socket", bad.ws.readyState === WebSocket.CLOSED);

  a.close();
  b.close();
} catch (error) {
  console.error(error);
  failures += 1;
}

console.log(failures === 0 ? "\nAll checks passed." : `\n${failures} check(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
