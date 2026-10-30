// 用 CDP 给页面截图：等 WS 真正连上、程序渲染完成后再截（比 --virtual-time-budget 可靠）。
// 用法: node shot_cdp.mjs <url> <outfile> [waitMs] [width] [height]
import { spawn } from "node:child_process";
import { writeFileSync } from "node:fs";
import http from "node:http";

const [, , url, outfile, waitMsArg, wArg, hArg] = process.argv;
const waitMs = Number(waitMsArg || 4000);
const width = Number(wArg || 1500);
const height = Number(hArg || 940);
const port = 9333;

const EDGE = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const profile = "D:\\zm\\ugu\\记得\\SFSVizzyMod\\tools\\cdp-prof";

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
await cdp("Runtime.enable");
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.method === "Runtime.consoleAPICalled") {
    const text = (m.params.args || []).map((a) => a.value ?? a.description ?? "").join(" ");
    console.log("[page]", m.params.type, text);
  } else if (m.method === "Runtime.exceptionThrown") {
    const d = m.params.exceptionDetails;
    console.log("[page-exception]", d.text, d.exception?.description || "");
  }
  if (m.id && pending.has(m.id)) {
    pending.get(m.id)(m);
    pending.delete(m.id);
  }
};
await cdp("Page.enable");
await cdp("Emulation.setDeviceMetricsOverride", {
  width, height, deviceScaleFactor: 1, mobile: false
});
await sleep(waitMs);

const shot = await cdp("Page.captureScreenshot", { format: "png" });
writeFileSync(outfile, Buffer.from(shot.result.data, "base64"));
console.log("saved", outfile);

ws.close();
proc.kill();
process.exit(0);
