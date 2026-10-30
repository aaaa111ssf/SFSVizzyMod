// 诊断视图切换问题
import { spawn } from "node:child_process";

const proc = spawn("C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", [
  "--headless=new", "--disable-gpu", "--no-first-run", "--remote-debugging-port=9334",
  "--user-data-dir=D:\\zm\\ugu\\记得\\SFSVizzyMod\\tools\\cdp-prof",
  "--window-size=1500,940", "http://127.0.0.1:8899/"
], { stdio: "ignore" });

const http = await import("node:http");
const get = (p) => new Promise((res, rej) => http.get({ host: "127.0.0.1", port: 9334, path: p }, r => {
  let d = ""; r.on("data", c => d += c); r.on("end", () => res(JSON.parse(d)));
}).on("error", rej));

let page = null;
for (let i = 0; i < 20; i++) {
  try {
    const t = await get("/json/list");
    page = t.find(x => x.type === "page" && x.url.startsWith("http"));
    if (page) break;
  } catch (e) {}
  await new Promise(r => setTimeout(r, 500));
}

const ws = new WebSocket(page.webSocketDebuggerUrl);
let id = 0; const pend = new Map();
ws.onmessage = ev => {
  const m = JSON.parse(ev.data);
  if (m.method === "Runtime.exceptionThrown") {
    const d = m.params.exceptionDetails;
    console.log("[page-exception]", d.text, d.exception?.description?.split("\n")[0] || "");
  }
  if (m.id && pend.has(m.id)) { pend.get(m.id)(m); pend.delete(m.id); }
};
const cdp = (method, params) => new Promise(res => { pend.set(++id, res); ws.send(JSON.stringify({ id, method, params: params || {} })); });
await new Promise(r => ws.onopen = r);
await cdp("Runtime.enable");
await new Promise(r => setTimeout(r, 3000));

const ev = async (label, expr) => {
  const r = await cdp("Runtime.evaluate", { expression: expr, returnByValue: true });
  console.log(label, "=>", JSON.stringify(r.result?.result?.value));
};

await ev("init view", "view");
await ev("to code", "document.querySelector('#viewCode').click(); view");
await ev("to blocks", "document.querySelector('#viewBlocks').click(); view");
await ev("codeErr text", "document.getElementById('codeErr').textContent");
await ev("codeErr cls", "document.getElementById('codeErr').className");
await ev("codeview hidden", "document.getElementById('codeview').hidden");
await ev("canvas hidden", "document.getElementById('canvas').hidden");

ws.close();
proc.kill();
process.exit(0);
