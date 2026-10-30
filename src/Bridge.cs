using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SFS.Input;
using SFS.World;
using UnityEngine;

namespace SFSVizzyMod
{
    // 游戏与浏览器之间的桥：本地服务 + 指令派发 + 遥测，Unity 对象只在主线程访问。
    public class Bridge : MonoBehaviour
    {
        public static Bridge main;

        public const int PortFrom = 8765;
        public const int PortTo = 8795;

        private WebServer server;
        private ProgramRunner runner;
        private bool lastRunning;
        private float stateTimer;
        private float lastToggle = -10f;

        // 浏览器最近一次同步过来的程序；按 R 时优先用它（浏览器离线才退回磁盘）。
        private ProgramData lastProgram;

        // >0 表示已向浏览器请求“用当前脚本运行”，超时则用缓存程序兜底。
        private float pendingRunAt = -1f;

        private float nextKeybindTry;

        public string Url => server != null && server.Port > 0 ? "http://127.0.0.1:" + server.Port + "/" : null;

        private void Awake()
        {
            main = this;
        }

        private void Start()
        {
            runner = GetComponent<ProgramRunner>();
            if (runner != null) runner.OnConsole += OnRunnerLog;

            EnsureSampleProgram();

            server = new WebServer { Html = WebAssets.IndexHtml() };
            server.Start(PortFrom, PortTo);

            if (server.Port > 0)
                Debug.Log("[SFSVizzy] Editor ready: " + Url + "  (press F8 to open in browser)");
        }

        private void OnDestroy()
        {
            if (runner != null) runner.OnConsole -= OnRunnerLog;
            server?.Stop();
        }

        private void Update()
        {
            if (server == null) return;

            // 原生按键系统可能比模组加载晚，未注册成功就定时重试
            if (!Keybindings.IsReady && Time.unscaledTime >= nextKeybindTry)
            {
                nextKeybindTry = Time.unscaledTime + 2f;
                Keybindings.Setup();
            }

            // 弹窗打开时不要抢键盘（否则输入 1/2 会触发快捷键）
            bool dialogOpen = DialogUI.main != null && DialogUI.main.IsOpen;

            // F8 打开浏览器（按键注册失败时退回写死的 F8）
            bool toggle = !dialogOpen && (Keybindings.main != null
                ? ((I_Key)Keybindings.main.toggleEditor).IsKeyDown()
                : Input.GetKeyDown(KeyCode.F8));
            if (toggle) OpenBrowser();

            // 游戏内快捷键：R 运行 / P 停止。键位在游戏「设置 → 按键」里改，读的是实时值。
            if (!dialogOpen)
            {
                if (RunHotkeyPressed()) RequestRun();
                if (StopHotkeyPressed() && runner != null && runner.IsRunning)
                    runner.StopProgram("stopped by hotkey");
            }

            // 网络线程 → 主线程：处理浏览器指令
            string message;
            while (server.Inbox.TryDequeue(out message)) Handle(message);

            // 浏览器没响应 RUN 请求时的兜底
            if (pendingRunAt > 0f && Time.unscaledTime > pendingRunAt)
            {
                pendingRunAt = -1f;
                Log("Browser did not answer — running last synced program");
                RunCachedProgram("hotkey fallback");
            }

            // 运行状态变化 → 通知浏览器
            bool running = runner != null && runner.IsRunning;
            if (running != lastRunning)
            {
                lastRunning = running;
                Broadcast(new JObject { ["type"] = "runstate", ["running"] = running });
            }

            // 遥测 10Hz
            stateTimer += Time.unscaledDeltaTime;
            if (stateTimer >= 0.1f)
            {
                stateTimer = 0f;
                if (server.HasClients) Broadcast(BuildState());
            }
        }

        public void OpenBrowser()
        {
            if (Time.unscaledTime - lastToggle < 0.4f) return;
            lastToggle = Time.unscaledTime;

            if (server == null || server.Port <= 0)
            {
                Debug.LogError("[SFSVizzy] Web server is not running");
                return;
            }
            try
            {
                Application.OpenURL(Url);
                Debug.Log("[SFSVizzy] Opened " + Url);
            }
            catch (Exception e)
            {
                Debug.LogError("[SFSVizzy] Could not open browser: " + e.Message);
            }
        }

        // ---------------- 快捷键 / 运行 ----------------

        private static bool RunHotkeyPressed()
        {
            if (Keybindings.main != null)
            {
                try { return ((I_Key)Keybindings.main.runProgram).IsKeyDown(); }
                catch (Exception) { }
            }
            return Input.GetKeyDown(KeyCode.R);
        }

        private static bool StopHotkeyPressed()
        {
            if (Keybindings.main != null)
            {
                try { return ((I_Key)Keybindings.main.stopProgram).IsKeyDown(); }
                catch (Exception) { }
            }
            return Input.GetKeyDown(KeyCode.P);
        }

        // 按 R：优先让浏览器把它“当前这一版”脚本发过来运行（可能还没落盘）；浏览器没连上就退回上次同步的程序 / 磁盘程序。0.8 秒内没回应也兜底。
        private void RequestRun()
        {
            if (runner == null) return;
            if (runner.IsRunning)
            {
                Log("RUN ignored: program already running");
                return;
            }
            if (server != null && server.HasClients)
            {
                pendingRunAt = Time.unscaledTime + 0.8f;
                Broadcast(new JObject { ["type"] = "request_run" });
                Log("RUN hotkey: requesting current program from browser…");
            }
            else
            {
                RunCachedProgram("hotkey, browser offline");
            }
        }

        private void RunCachedProgram(string source)
        {
            if (runner == null) return;
            ProgramData p = lastProgram;
            if (p == null)
            {
                try { p = ProgramIO.Load(); }
                catch (Exception e) { Log("Run failed: " + e.Message); return; }
            }
            if (p == null) { Log("Run failed: no program yet"); return; }
            if (WorldVars.GetPlayerRocket() == null)
            {
                Log("Run failed: no rocket in flight scene");
                return;
            }
            Log("RUN (" + source + ")");
            runner.StartProgram(p);
        }

        // ---------------- 指令 ----------------

        private void Handle(string json)
        {
            try
            {
                JObject msg = JObject.Parse(json);
                string type = (string)msg["type"] ?? "";

                switch (type)
                {
                    case "hello":
                        SendProgram();
                        Broadcast(new JObject { ["type"] = "runstate", ["running"] = runner != null && runner.IsRunning });
                        break;

                    case "run":
                    {
                        ProgramData p = msg["program"]?.ToObject<ProgramData>();
                        if (p == null) { Log("Run failed: empty program"); break; }
                        pendingRunAt = -1f;
                        lastProgram = p;
                        if (WorldVars.GetPlayerRocket() == null)
                        {
                            Log("Run failed: no rocket in flight scene");
                            break;
                        }
                        runner.StartProgram(p);
                        break;
                    }

                    case "stop":
                        runner.StopProgram("stopped from browser");
                        break;

                    case "save":
                    {
                        ProgramData p = msg["program"]?.ToObject<ProgramData>();
                        if (p == null) break;
                        lastProgram = p;
                        ProgramIO.Save(p);
                        Log("Program saved to Mods/SFSVizzyMod/program.json");
                        break;
                    }

                    case "get":
                        SendProgram();
                        break;
                }
            }
            catch (Exception e)
            {
                Log("Bad command: " + e.Message);
            }
        }

        private void SendProgram()
        {
            JObject o = new JObject { ["type"] = "program" };
            try { o["program"] = JToken.Parse(ProgramIO.LoadRaw()); }
            catch (Exception) { o["program"] = JToken.Parse("{\"main\":[],\"variables\":{}}"); }
            Broadcast(o);
        }

        // ---------------- 遥测 ----------------

        private JObject BuildState()
        {
            JObject o = new JObject
            {
                ["type"] = "state",
                ["running"] = runner != null && runner.IsRunning,
                ["t"] = WorldTime.main != null ? WorldTime.main.worldTime : 0.0
            };

            try
            {
                Rocket r = WorldVars.GetPlayerRocket();
                o["rocket"] = r != null;
                o["situation"] = r != null ? r.floating ? "water" : "flight" : "none";

                if (r != null)
                {
                    Dictionary<string, double> vars = new Dictionary<string, double>();
                    WorldVars.Fill(r, vars);
                    foreach (KeyValuePair<string, double> kv in vars)
                    {
                        // NaN/Infinity 会序列化成非法 JSON，浏览器 JSON.parse 直接抛异常，整条遥测被丢 → 坠毁/重发射瞬间面板冻结。这里一律滤掉。
                        if (double.IsFinite(kv.Value)) o[kv.Key] = Math.Round(kv.Value, 3);
                    }

                    try { o["planet"] = (string)r.location.planet.Value.DisplayName; }
                    catch (Exception) { o["planet"] = "?"; }
                }
            }
            catch (Exception)
            {
                o["rocket"] = false;
            }
            return o;
        }

        // ---------------- 日志 ----------------

        private void OnRunnerLog(string line)
        {
            Log(line);
        }

        public void Log(string line)
        {
            Debug.Log("[SFSVizzy] " + line);
            Broadcast(new JObject { ["type"] = "log", ["line"] = line });
        }

        private void Broadcast(JObject o)
        {
            server?.Broadcast(o.ToString(Newtonsoft.Json.Formatting.None));
        }

        // ---------------- 首次示例 ----------------

        private static void EnsureSampleProgram()
        {
            try
            {
                if (ProgramIO.Exists()) return;

                ProgramData p = new ProgramData();
                LaunchEvent launch = new LaunchEvent();

                launch.blocks.Add(new Block { kind = Kinds.Wait, a = "1" });
                launch.blocks.Add(new Block { kind = Kinds.SetThrottle, a = "1" });
                launch.blocks.Add(new Block { kind = Kinds.Log, a = "Liftoff at {AltitudeAGL} m!" });

                Block loop = new Block
                {
                    kind = Kinds.While,
                    a = "AltitudeAGL",
                    op = "<=",
                    b = "10000"
                };

                Block branch = new Block
                {
                    kind = Kinds.If,
                    a = "TWR",
                    op = ">=",
                    b = "1.2"
                };
                branch.then.Add(new Block { kind = Kinds.SetThrottle, a = "Throttle - 0.05" });
                branch.then.Add(new Block { kind = Kinds.Log, a = "Reduced thrust" });
                branch.els.Add(new Block { kind = Kinds.SetThrottle, a = "Throttle + 0.05" });
                branch.els.Add(new Block { kind = Kinds.Log, a = "Increased thrust" });

                loop.body.Add(branch);
                loop.body.Add(new Block { kind = Kinds.Wait, a = "0.2" });
                launch.blocks.Add(loop);

                launch.blocks.Add(new Block { kind = Kinds.Log, a = "Ascent complete" });
                p.launches.Add(launch);

                // 高度触发示例：真实离地高度（AGL，极坐标径向、含地形）到 5 km 时报一次
                Trigger trigger = new Trigger { mode = "agl", op = ">=", value = "5000", once = true };
                trigger.blocks.Add(new Block { kind = Kinds.Log, a = "Trigger: 5 km true altitude reached ({AltitudeAGL} m)" });
                p.triggers.Add(trigger);

                ProgramIO.Save(p);
                Debug.Log("[SFSVizzy] Wrote sample program");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SFSVizzy] Could not write sample program: " + e.Message);
            }
        }
    }
}
