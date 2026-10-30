// 多视图验收截图：积木视图 / 代码视图 / 帮助弹窗。
// 用法: node shot_views.mjs <url> <outdir>
import { spawn } from "node:child_process";
import { writeFileSync } from "node:fs";
import http from "node:http";

const [, , url, outdir] = process.argv;
const width = 1500, height = 940, port = 9391 + (process.pid % 50);

const EDGE = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const profile = "D:\\zm\\ugu\\记得\\SFSVizzyMod\\tools\\cdp-prof-shot-" + process.pid;

const proc = spawn(EDGE, [
  "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
  `--remote-debugging-port=${port}`,
  `--user-data-dir=${profile}`,
  `--window-size=${width},${height}`,
  url
], { stdio: "ignore" });

function getJson(path) {
  return new Promise((resolve, reject) => {
    http.get({ host: "127.0.0.1", port, path }, (res) => {
      let data = "";
      res.on("data", (c) => (data += c));
      res.on("end", () => resolve(JSON.parse(data)));
    }).on("error", reject);
  });
}

async function findPage(retries) {
  for (let i = 0; i < retries; i++) {
    try {
      const targets = await getJson("/json/list");
      const page = targets.find((t) => t.type === "page" && t.url.startsWith("http"));
      if (page) return page;
    } catch (_) {}
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error("page target not found");
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const target = await findPage(20);
const ws = new WebSocket(target.webSocketDebuggerUrl);

let msgId = 0;
const pending = new Map();
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) {
    pending.get(m.id)(m);
    pending.delete(m.id);
  }
};
function cdp(method, params) {
  const id = ++msgId;
  return new Promise((resolve) => {
    pending.set(id, resolve);
    ws.send(JSON.stringify({ id, method, params: params || {} }));
  });
}

await new Promise((r) => (ws.onopen = r));
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.method === "Runtime.exceptionThrown") {
    const d = m.params.exceptionDetails;
    console.log("[page-exception]", d.text, d.exception?.description || "");
  }
  if (m.id && pending.has(m.id)) {
    pending.get(m.id)(m);
    pending.delete(m.id);
  }
};
await cdp("Runtime.enable");
await cdp("Page.enable");
await cdp("Page.navigate", { url });
await sleep(3000);
await cdp("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false });
await sleep(1200);

async function shot(name) {
  const s = await cdp("Page.captureScreenshot", { format: "png" });
  writeFileSync(`${outdir}/${name}.png`, Buffer.from(s.result.data, "base64"));
  console.log("saved", name);
}

async function click(sel) {
  const r = await cdp("Runtime.evaluate", {
    expression: `(() => { const b = document.querySelector(${JSON.stringify(sel)}); if (!b) return "missing"; b.click(); return "ok"; })()`,
    returnByValue: true
  });
  console.log("click", sel, r.result?.result?.value);
  await sleep(700);
}

// 以游戏侧程序为准（否则会读到浏览器 localStorage 里的旧程序）
await click("#btnFromGame");
await sleep(900);
await shot("view_blocks");
await click("#viewCode");
await shot("view_code");
await click("#viewBlocks");
await click("#btnHelp");
await shot("view_help");
await click("#btnHelpClose");
await click("#btnMechJeb");
await shot("view_mechjeb");

ws.close();
proc.kill();
process.exit(0);
