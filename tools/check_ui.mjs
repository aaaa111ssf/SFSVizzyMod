// UI 逻辑验收：代码往返、触发器数量、request_run 握手。需 mock_server.py 在 8899 运行。
import { spawn } from "node:child_process";
import http from "node:http";

const url = process.argv[2] || "http://127.0.0.1:8899/";
const EDGE = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const profile = "D:\\zm\\ugu\\记得\\SFSVizzyMod\\tools\\cdp-prof-check-" + process.pid;
const port = 9341 + (process.pid % 50);

const proc = spawn(EDGE, ["--headless=new", "--disable-gpu", "--no-first-run",
  `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
  "--window-size=1500,940", url], { stdio: "ignore" });

const getJson = (path) => new Promise((resolve, reject) => {
  http.get({ host: "127.0.0.1", port, path }, (res) => {
    let d = ""; res.on("data", (c) => (d += c)); res.on("end", () => resolve(JSON.parse(d)));
  }).on("error", reject);
});

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let target;
for (let i = 0; i < 30 && !target; i++) {
  try {
    const list = await getJson("/json/list");
    target = list.find((t) => t.type === "page" && t.url.startsWith("http"));
  } catch (_) {}
  if (!target) await sleep(400);
}

const ws = new WebSocket(target.webSocketDebuggerUrl);
let id = 0;
const pending = new Map();
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
};
const cdp = (method, params) => new Promise((resolve) => {
  const n = ++id; pending.set(n, resolve);
  ws.send(JSON.stringify({ id: n, method, params: params || {} }));
});
const evaluate = async (expr) => {
  const r = await cdp("Runtime.evaluate", { expression: expr, returnByValue: true, awaitPromise: true });
  if (!r.result || !r.result.result) {
    console.log("EVAL-ERR", JSON.stringify(r).slice(0, 600));
    return undefined;
  }
  if (r.result.exceptionDetails) {
    console.log("PAGE-EXCEPTION", JSON.stringify(r.result.exceptionDetails).slice(0, 600));
    return undefined;
  }
  return r.result.result.value;
};

await new Promise((r) => (ws.onopen = r));
await cdp("Runtime.enable");
await cdp("Page.enable");
await cdp("Page.navigate", { url });
await sleep(4000);
await evaluate(`document.getElementById('btnFromGame').click()`);
await sleep(1000);

console.log("probe:", await evaluate(`document.title + " | " + location.href + " | scripts=" + document.scripts.length + " | len=" + (document.scripts[0] ? document.scripts[0].textContent.length : -1) + " | hasBtn=" + !!document.getElementById("btnRecenter")`));
console.log("probe2:", await evaluate(`typeof program + " | " + document.readyState + " | " + (typeof viewState)`));
console.log("ui-style:", await evaluate(`(() => {
  const ev = document.querySelector('.eventbar');
  return JSON.stringify({
    launches: (program.launches || []).length,
    eventbars: document.querySelectorAll('.eventbar').length,
    evBg: ev ? getComputedStyle(ev).backgroundColor : null,
    evColor: ev ? getComputedStyle(ev).color : null,
    canvasBg: getComputedStyle(document.getElementById('canvas')).backgroundColor,
    blocks: document.querySelectorAll('.block').length
  });
})()`));

console.log("panels:", await evaluate(`(() => {
  const fuel = (telemValues["Fuel"] || {}).textContent;
  document.getElementById("btnTogglePalette").click();
  const leftHidden = document.body.classList.contains("left-hidden");
  document.getElementById("btnTogglePalette").click();
  document.getElementById("btnToggleBottom").click();
  const bottomHidden = document.body.classList.contains("bottom-hidden");
  document.getElementById("btnToggleBottom").click();
  return JSON.stringify({ fuel, leftHidden, bottomHidden,
    palette: !!document.getElementById("palette"), bottom: !!document.getElementById("bottom") });
})()`));

console.log("state:", await evaluate(`JSON.stringify({
  connected: !!ws && ws.readyState === 1,
  launches: (program.launches || []).length,
  events: (program.events || []).length,
  triggers: (program.triggers || []).length,
  blocks: totalBlocks(),
  turnDir: (((program.triggers[0] || {}).blocks || []).find(b => b.kind === 'turn') || {}).dir || null
})`));

console.log("view:", await evaluate(`(() => {
  const before = viewState.x + "," + viewState.y + "," + viewState.z;
  viewState.x = 100; viewState.y = 40; viewState.z = 1.2; applyView();
  const t = document.getElementById('canvasWorld').style.transform;
  document.getElementById('btnRecenter').click();
  return JSON.stringify({ before, transform: t, afterReset: viewState.x + "," + viewState.y + "," + viewState.z });
})()`));

console.log("zoom:", await evaluate(`(() => {
  const z0 = viewState.z;
  document.getElementById('zoomIn').click();
  document.getElementById('zoomIn').click();
  const z1 = viewState.z;
  const label = document.getElementById('zoomPct').textContent;
  document.getElementById('zoomOut').click();
  const z2 = viewState.z;
  document.getElementById('btnRecenter').click();
  return JSON.stringify({ z0, z1, z2, label, reset: viewState.z,
    mjPanel: !!document.getElementById('mjModal'),
    mjButtons: document.querySelectorAll('[data-mj]').length });
})()`));

console.log("roundtrip:", await evaluate(`(() => {
  codeTextEl.value = programToCode();
  const ok = applyCode(false);
  return JSON.stringify({ ok, error: codeErrEl.textContent,
    launches: (program.launches || []).length,
    events: (program.events || []).length,
    triggers: (program.triggers || []).length,
    once: (program.triggers[0] || {}).once,
    turnInTrigger: ((program.triggers[0] || {}).blocks || []).some(b => b.kind === 'turn') });
})()`));

console.log("console tail:", await evaluate(`Array.from(document.querySelectorAll('#console div')).slice(-4).map(d => d.textContent).join(' | ')`));

ws.close();
proc.kill();
process.exit(0);
