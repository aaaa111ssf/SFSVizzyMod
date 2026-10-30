// 用页面自身的 parseCode 校验 examples/landing.txt 是否可解析
import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import http from "node:http";

const url = process.argv[2] || "http://127.0.0.1:8899/";
const file = process.argv[3] || new URL("../examples/landing.txt", import.meta.url).pathname;
const code = readFileSync(file, "utf8");
const EDGE = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const profile = "D:\\zm\\ugu\\记得\\SFSVizzyMod\\tools\\cdp-prof-landing-" + process.pid;
const port = 9334;

const proc = spawn(EDGE, ["--headless=new", "--disable-gpu", "--no-first-run",
  `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
  "--window-size=1400,900", url], { stdio: "ignore" });

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
    target = list.find((t) => t.type === "page");
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
  const r = await cdp("Runtime.evaluate", { expression: expr, returnByValue: true });
  if (!r.result || !r.result.result) {
    console.log("EVAL-ERR", JSON.stringify(r).slice(0, 400));
    return undefined;
  }
  if (r.result.exceptionDetails) {
    console.log("PAGE-EXC", JSON.stringify(r.result.exceptionDetails).slice(0, 400));
    return undefined;
  }
  return r.result.result.value;
};

await new Promise((r) => (ws.onopen = r));
await cdp("Runtime.enable");
await cdp("Page.enable");
await cdp("Page.navigate", { url });
await sleep(3500);

const lit = JSON.stringify(code);
console.log("parse:", await evaluate(`(() => {
  codeTextEl.value = ${lit};
  const ok = applyCode(false);
  const kinds = [];
  const walk = (list) => { for (const b of list) { kinds.push(b.kind); if (b.kind === 'if') { walk(b.then||[]); walk(b.els||[]); } if (b.kind === 'while' || b.kind === 'repeat') walk(b.body||[]); } };
  walk(program.launches[0].blocks);
  return JSON.stringify({ ok, error: codeErrEl.textContent, blocks: totalBlocks(), kinds });
})()`));

ws.close();
proc.kill();
process.exit(0);
