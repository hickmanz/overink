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
  check("loopback receives a device key", typeof a.welcome.deviceKey === "string");
  check("welcome state has tool", typeof a.welcome.state.tool === "string");

  const roster = await b.next("roster", (m) => m.participants.length === 2);
  check("roster lists both clients", roster.participants.length === 2);

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
  a.send({ t: "setColor", color: "#8E24AA" });
  const state = await b.next("state", (m) => m.color === "#8E24AA");
  check("setColor reaches other clients", state.color === "#8E24AA");

  a.send({ t: "setColor", color: a.welcome.state.color }); // put the user's colour back

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
