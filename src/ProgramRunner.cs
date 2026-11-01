using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using SFS.Parts;
using SFS.Parts.Modules;
using SFS.UI;
using SFS.World;
using UnityEngine;

namespace SFSVizzyMod
{
    // 积木程序解释器。用 Unity 协程实现：每条语句至少跨一帧执行，天然防止 while 死循环冻住游戏；Wait 使用游戏时间（可被时间加速推进）。
    public class ProgramRunner : MonoBehaviour
    {
        public static ProgramRunner main;

        public bool IsRunning { get; private set; }

        // 编辑器订阅这个事件显示控制台输出。
        public event Action<string> OnConsole;

        private Coroutine routine;
        private ProgramData program;
        private readonly Dictionary<string, double> userVars = new Dictionary<string, double>();
        private readonly Dictionary<string, double> vars = new Dictionary<string, double>();
        private bool hadRocket;

        private static readonly Regex Placeholder = new Regex("\\{([^{}]+)\\}", RegexOptions.Compiled);
        private int stageIdentifier;

        private readonly List<Coroutine> launchRoutines = new List<Coroutine>();
        private readonly List<Coroutine> extraRoutines = new List<Coroutine>();
        private readonly List<TriggerState> triggerStates = new List<TriggerState>();
        private int pendingLaunches;
        private readonly List<EventState> eventStates = new List<EventState>();
        private readonly Dictionary<string, FunctionDef> functions = new Dictionary<string, FunctionDef>();
        private int callDepth;
        private int crashSerial;
        private float wheelDrive;
        private float wheelDriveUntil;
        private Vector2 rcsDrive;
        private float rcsUntil;

        // 每个 ON START / 触发器各自一份，break 不会串到别的并行脚本。
        private sealed class RunContext
        {
            public bool breakFlag;
            public bool continueFlag;
        }

        private sealed class EventState
        {
            public GameEvent cfg;
            public bool fired;
            public int lastStage;
            public bool lastLanded;
            public bool lastDock;
            public int lastCrash;
            public Rocket lastRocket;
        }

        // 高度每帧只采样一次（多个触发器 / 多个积木共用），避免重复查询地形高度
        private int altitudeFrame = -1;
        private double cachedAgl;
        private double cachedAsl;

        // 高度触发器的运行时状态。
        private sealed class TriggerState
        {
            public Trigger cfg;
            public double threshold;
            public bool fired;
            public bool hasPrev;
            public double prev;
        }

        private void Awake()
        {
            main = this;
        }

        // ---------------- 控制接口 ----------------

        public void StartProgram(ProgramData data)
        {
            StopProgram(null);
            if (data == null) return;

            program = data;
            userVars.Clear();
            if (data.variables != null)
                foreach (KeyValuePair<string, double> kv in data.variables)
                    userVars[kv.Key] = kv.Value;

            vars.Clear();
            hadRocket = false;
            IsRunning = true;

            // 先解析触发器阈值（表达式错误会立刻停机），再启动任何协程
            triggerStates.Clear();
            if (data.triggers != null)
            {
                for (int i = 0; i < data.triggers.Count; i++)
                {
                    Trigger t = data.triggers[i];
                    if (t == null) continue;
                    double threshold = Eval(t.value);
                    if (!IsRunning) return;
                    triggerStates.Add(new TriggerState { cfg = t, threshold = threshold });
                }
            }

            for (int i = 0; i < triggerStates.Count; i++)
                extraRoutines.Add(StartCoroutine(WatchTrigger(triggerStates[i], i)));

            functions.Clear();
            if (data.functions != null)
                for (int i = 0; i < data.functions.Count; i++)
                {
                    FunctionDef f = data.functions[i];
                    if (f != null && !string.IsNullOrEmpty(f.name)) functions[f.name] = f;
                }

            functions.Clear();
            if (data.functions != null)
                for (int i = 0; i < data.functions.Count; i++)
                {
                    FunctionDef f = data.functions[i];
                    if (f != null && !string.IsNullOrEmpty(f.name)) functions[f.name] = f;
                }
            callDepth = 0;

            eventStates.Clear();
            if (data.events != null)
            {
                for (int i = 0; i < data.events.Count; i++)
                {
                    GameEvent ev = data.events[i];
                    if (ev == null) continue;
                    EventState st = new EventState
                    {
                        cfg = ev,
                        lastStage = StageCount(),
                        lastLanded = IsLanded(),
                        lastDock = IsDocked(),
                        lastCrash = crashSerial,
                        lastRocket = WorldVars.GetPlayerRocket()
                    };
                    eventStates.Add(st);
                    extraRoutines.Add(StartCoroutine(WatchEvent(st)));
                }
            }

            List<LaunchEvent> launches = data.GetLaunches();
            pendingLaunches = launches.Count;
            Print("--- Program started (" + launches.Count + " ON START) ---");

            for (int i = 0; i < launches.Count; i++)
            {
                List<Block> blocks = launches[i] != null ? launches[i].blocks : null;
                launchRoutines.Add(StartCoroutine(RunLaunch(blocks, new RunContext())));
            }

            // 没有任何 ON START 时也要走一次完成流程（可能只剩触发器在等）
            if (pendingLaunches == 0)
            {
                pendingLaunches = 1;
                launchRoutines.Add(StartCoroutine(RunLaunch(new List<Block>(), new RunContext())));
            }
        }

        public void StopProgram(string reason)
        {
            StopRoutines();
            bool wasRunning = IsRunning;
            IsRunning = false;
            if (DialogUI.main != null) DialogUI.main.Close(false);
            if (WorldTime.main != null && WorldTime.main.timewarpIndex > 0) WorldTime.main.StopTimewarp(false);
            triggerStates.Clear();
            functions.Clear();
            callDepth = 0;
            eventStates.Clear();
            pendingLaunches = 0;
            ReleaseControls();
            if (wasRunning && !string.IsNullOrEmpty(reason))
                Print("STOPPED: " + reason);
        }

        private void StopRoutines()
        {
            if (routine != null)
            {
                StopCoroutine(routine);
                routine = null;
            }
            for (int i = 0; i < launchRoutines.Count; i++)
                if (launchRoutines[i] != null) StopCoroutine(launchRoutines[i]);
            launchRoutines.Clear();

            for (int i = 0; i < extraRoutines.Count; i++)
                if (extraRoutines[i] != null) StopCoroutine(extraRoutines[i]);
            extraRoutines.Clear();
        }

        private void Finish(string reason)
        {
            StopRoutines();
            IsRunning = false;
            triggerStates.Clear();
            pendingLaunches = 0;
            ReleaseControls();
            Print(reason);
        }

        // 松开转向输入：不归零的话火箭会一直带着角速度转。
        private void ReleaseControls()
        {
            try
            {
                Rocket r = WorldVars.GetPlayerRocket();
                if (r != null && r.arrowkeys != null) r.arrowkeys.turnAxis.Value = 0f;
            }
            catch (Exception) { }
        }

        private Block currentBlock;

        private void Fail(string msg)
        {
            Print("FAILED: " + msg);
            if (Bridge.main != null) Bridge.main.SendError(msg, currentBlock);
            if (MsgDrawer.main != null) MsgDrawer.main.Log("SFS Vizzy: " + msg, true);
            Finish("Program aborted");
        }

        public void Print(string msg)
        {
            OnConsole?.Invoke(msg);
            Debug.Log("[SFSVizzy] " + msg);
        }

        // ---------------- 主循环 ----------------

        private IEnumerator RunLaunch(List<Block> blocks, RunContext ctx)
        {
            yield return RunList(blocks, ctx);
            if (!IsRunning) yield break;

            pendingLaunches--;
            if (pendingLaunches > 0) yield break;

            int armed = ArmedTriggerCount();
            if (armed > 0)
            {
                Print("--- All ON START scripts finished; " + armed + " altitude trigger(s) armed ---");
                yield break;
            }

            // 有事件监听就保持运行，否则按一次键、分一次级程序早就退出了
            if (eventStates.Count > 0)
            {
                Print("--- Waiting for events (" + eventStates.Count + "); press STOP to end ---");
                yield break;
            }

            Finish("--- Program finished ---");
        }

        private int ArmedTriggerCount()
        {
            int n = 0;
            for (int i = 0; i < triggerStates.Count; i++)
            {
                TriggerState st = triggerStates[i];
                if (st.cfg.once && st.fired) continue;
                n++;
            }
            return n;
        }

        // 高度触发器：每帧采样，条件由 false 变 true 时执行一次 blocks。once=false 时离开阈值（2 m 回差）后重新武装，避免阈值抖动连续触发。
        private IEnumerator WatchEvent(EventState st)
        {
            yield return null;   // 让启动程序那一下的按键先过去
            while (IsRunning)
            {
                Rocket r = WorldVars.GetPlayerRocket();
                bool fire = false;
                string type = st.cfg.type ?? "key";

                switch (type)
                {
                    case "key":
                        if (r != null && !string.IsNullOrEmpty(st.cfg.key)
                            && !(DialogUI.main != null && DialogUI.main.IsOpen))
                        {
                            KeyCode code;
                            if (System.Enum.TryParse<KeyCode>(st.cfg.key, true, out code))
                                fire = Input.GetKeyDown(code);
                        }
                        break;

                    case "stage":
                        if (r != null)
                        {
                            int n = StageCount();
                            if (n < st.lastStage) fire = true;
                            st.lastStage = n;
                        }
                        break;

                    case "landed":
                        if (r != null)
                        {
                            bool v = IsLanded();
                            if (v && !st.lastLanded) fire = true;
                            st.lastLanded = v;
                        }
                        break;

                    case "dock":
                        if (r != null)
                        {
                            bool v = IsDocked();
                            if (v && !st.lastDock) fire = true;
                            st.lastDock = v;
                        }
                        break;

                    case "crash":
                    {
                        if (crashSerial != st.lastCrash)
                        {
                            fire = true;
                            st.lastCrash = crashSerial;
                        }
                        else
                        {
                            // 部件逐个爆、关节断裂时 DestroyRocket 不一定调用，玩家会被切到残骸
                            Rocket cur = WorldVars.GetPlayerRocket();
                            if (cur != st.lastRocket)
                            {
                                fire = true;
                                st.lastRocket = cur;
                                Print("ROCKET CHANGED: " + (cur == null ? "null" : "child rocket"));
                            }
                        }
                        break;
                    }
                }

                if (fire && !(st.cfg.once && st.fired))
                {
                    st.fired = true;
                    Print("EVENT: " + type);
                    StartCoroutine(RunList(st.cfg.blocks ?? new List<Block>(), new RunContext()));
                }

                yield return null;
            }
        }

        private int StageCount()
        {
            Rocket r = WorldVars.GetPlayerRocket();
            if (r == null) return 0;
            int n = 0;
            foreach (Stage st in r.staging.stages)
                if (st != null && st.PartCount > 0) n++;
            return n;
        }

        private bool IsLanded()
        {
            Rocket r = WorldVars.GetPlayerRocket();
            try { return r != null && r.stats != null && r.stats.tracker.state_Landed; }
            catch (Exception) { return false; }
        }

        private bool IsDocked()
        {
            Rocket r = WorldVars.GetPlayerRocket();
            if (r == null) return false;
            try
            {
                DockingPortModule[] ports = r.partHolder.GetModules<DockingPortModule>();
                for (int i = 0; i < ports.Length; i++)
                    if (ports[i] != null && ports[i].isOccupied.Value) return true;
            }
            catch (Exception) { }
            return false;
        }

        private IEnumerator WatchTrigger(TriggerState st, int index)
        {
            Trigger t = st.cfg;
            bool repeat = !t.once;

            while (IsRunning)
            {
                CheckRocket();
                if (!IsRunning) yield break;

                double alt = ReadAltitude(t.mode);
                if (!double.IsFinite(alt))
                {
                    yield return null;
                    continue;
                }

                if (!st.hasPrev)
                {
                    st.prev = alt;
                    st.hasPrev = true;
                    yield return null;
                    continue;
                }

                bool now = CompareAltitude(alt, t.op, st.threshold);
                bool was = CompareAltitude(st.prev, t.op, st.threshold);

                if (!st.fired && !was && now)
                {
                    st.fired = true;
                    Print("TRIGGER " + (index + 1) + ": " + (t.mode == "asl" ? "ASL" : "AGL") + " "
                        + t.op + " " + st.threshold.ToString("0.###") + " m reached (current "
                        + alt.ToString("0") + " m)");

                    // 触发器体与主程序并发执行；break 标记必须隔离，不能泄漏到主程序容器；触发器体与其它 ON START 并发执行；各自一份 RunContext，break 不会串台
                    yield return RunList(t.blocks, new RunContext());

                    if (!IsRunning) yield break;
                    if (!repeat) yield break;   // once：结束监听
                }
                else if (repeat && st.fired)
                {
                    // 离开阈值 2 m 后重新武装
                    bool left = t.op.StartsWith("<")
                        ? alt > st.threshold + 2.0
                        : alt < st.threshold - 2.0;
                    if (left) st.fired = false;
                }

                st.prev = alt;
                yield return null;
            }
        }

        private static bool CompareAltitude(double value, string op, double threshold)
        {
            switch (op)
            {
                case "<": return value < threshold;
                case "<=": return value <= threshold;
                case ">": return value > threshold;
                case ">=": return value >= threshold;
                case "==": return Math.Abs(value - threshold) <= 1e-6;
                case "!=": return Math.Abs(value - threshold) > 1e-6;
                default: return false;
            }
        }

        // 单帧缓存的高度采样。AGL 用 clampToWater:true，与游戏 HUD / AltitudeAGL 变量完全同口径；ASL 用 Location.Height。
        private void SampleAltitude()
        {
            if (altitudeFrame == Time.frameCount) return;
            altitudeFrame = Time.frameCount;
            cachedAgl = double.NaN;
            cachedAsl = double.NaN;

            Rocket r = WorldVars.GetPlayerRocket();
            if (r == null) return;
            try
            {
                Location loc = r.location.Value;
                cachedAsl = loc.Height;
                cachedAgl = loc.GetTerrainHeight(true);
            }
            catch (Exception) { }
        }

        private double ReadAltitude(string mode)
        {
            SampleAltitude();
            return mode == "asl" ? cachedAsl : cachedAgl;
        }

        private IEnumerator RunList(List<Block> list, RunContext ctx)
        {
            if (list == null || list.Count == 0) yield break;

            // 快照一份：编辑期间增删积木不会让 foreach 崩溃
            Block[] snapshot = list.ToArray();
            foreach (Block b in snapshot)
            {
                if (!IsRunning) yield break;

                yield return RunBlock(b, ctx);
                if (!IsRunning) yield break;

                // continue 只上抛，由最近的循环消费
                if (ctx.continueFlag) yield break;

                // break 只中止当前容器，由最近的 while/repeat 消费
                if (ctx.breakFlag) yield break;

                CheckRocket();
                if (!IsRunning) yield break;

                // 每条语句至少跨一帧 —— 防止空 while 死循环冻住游戏
                yield return null;
            }
        }

        private IEnumerator RunBlock(Block b, RunContext ctx)
        {
            currentBlock = b;
            if (b == null || string.IsNullOrEmpty(b.kind)) yield break;

            switch (b.kind)
            {
                case Kinds.Wait:
                {
                    double secs = Eval(b.a);
                    if (!IsRunning) yield break;
                    if (secs > 0.0)
                    {
                        double deadline = GameTime() + secs;
                        while (GameTime() < deadline)
                        {
                            if (!IsRunning) yield break;
                            yield return null;
                        }
                    }
                    break;
                }

                case Kinds.WaitUntil:
                {
                    while (!Cond(b))
                    {
                        if (!IsRunning) yield break;
                        yield return null;
                    }
                    break;
                }

                case Kinds.SetThrottle:
                {
                    Rocket r = WorldVars.GetPlayerRocket();
                    if (r == null) { Fail("set throttle: no rocket"); yield break; }
                    double v = Eval(b.a);
                    if (!IsRunning) yield break;
                    r.throttle.throttlePercent.Value = Mathf.Clamp01((float)v);
                    if (v > 0.001) r.throttle.throttleOn.Value = true;
                    break;
                }

                case Kinds.ThrottleOn:
                {
                    Rocket r = WorldVars.GetPlayerRocket();
                    if (r == null) { Fail("throttle on: no rocket"); yield break; }
                    r.throttle.throttleOn.Value = true;
                    break;
                }

                case Kinds.ThrottleOff:
                {
                    Rocket r = WorldVars.GetPlayerRocket();
                    if (r == null) { Fail("throttle off: no rocket"); yield break; }
                    r.throttle.throttleOn.Value = false;
                    break;
                }

                case Kinds.RcsOn:
                {
                    Rocket r = WorldVars.GetPlayerRocket();
                    if (r == null) { Fail("rcs on: no rocket"); yield break; }
                    r.arrowkeys.rcs.Value = true;
                    break;
                }

                case Kinds.RcsOff:
                {
                    Rocket r = WorldVars.GetPlayerRocket();
                    if (r == null) { Fail("rcs off: no rocket"); yield break; }
                    r.arrowkeys.rcs.Value = false;
                    break;
                }

                case Kinds.Turn:
                {
                    Rocket r1 = WorldVars.GetPlayerRocket();
                    if (r1 == null) { Fail("turn: no rocket"); yield break; }

                    double angle = Eval(b.a);
                    if (!IsRunning) yield break;
                    double duration = OptionalDuration(b);
                    if (!IsRunning) yield break;

                    // 基准 = 90° 垂直上升姿态（当地天顶的推力方向），而不是当前机头。left  = 天顶 + 角度（逆时针，屏幕上向左）；right = 天顶 - 角度（顺时针，屏幕上向右）；缺省 rel = 相对当前机头（旧存档兼容）
                    string dir = string.IsNullOrEmpty(b.dir) ? "rel" : b.dir.ToLowerInvariant();
                    double target;
                    if (dir == "rel")
                    {
                        target = r1.GetRotation() + angle;
                    }
                    else
                    {
                        Location loc1 = r1.location.Value;
                        double sign = dir == "right" ? -1.0 : 1.0;
                        target = WorldVars.ZenithAngle(loc1) + sign * Math.Abs(angle);
                    }
                    yield return TurnTo(r1, target, duration);
                    break;
                }

                case Kinds.TurnTo:
                {
                    Rocket r2 = WorldVars.GetPlayerRocket();
                    if (r2 == null) { Fail("turn to: no rocket"); yield break; }

                    Location loc = r2.location.Value;
                    string dir = string.IsNullOrEmpty(b.dir) ? "deg" : b.dir;
                    double worldTarget;
                    switch (dir)
                    {
                        case "up": worldTarget = WorldVars.ZenithAngle(loc); break;
                        case "down": worldTarget = WorldVars.ZenithAngle(loc) + 180.0; break;
                        case "prograde": worldTarget = WorldVars.VelocityAngle(loc); break;
                        case "retrograde": worldTarget = WorldVars.VelocityAngle(loc) + 180.0; break;
                        case "target":
                            if (WorldVars.GetTarget() == null) { Fail("turn to target: no target selected in map"); yield break; }
                            worldTarget = WorldVars.TargetWorldAngle(loc);
                            break;
                        default:
                        {
                            double rel = Eval(b.a);
                            if (!IsRunning) yield break;
                            // deg 与 turn 同一基准：0 = 天顶，正值 = 向左
                            worldTarget = WorldVars.ZenithAngle(loc) + rel;
                            break;
                        }
                    }
                    double duration = OptionalDuration(b);
                    if (!IsRunning) yield break;
                    yield return TurnTo(r2, worldTarget, duration);
                    break;
                }

                case Kinds.Stage:
                {
                    Rocket r = WorldVars.GetPlayerRocket();
                    if (r == null) { Fail("stage: no rocket"); yield break; }
                    int ordinal = 1;
                    if (!string.IsNullOrEmpty(b.a)) int.TryParse(b.a.Trim(), out ordinal);
                    ActivateStage(r, Math.Max(1, ordinal));
                    break;
                }

                case Kinds.Engine:
                {
                    Rocket r = WorldVars.GetPlayerRocket();
                    if (r == null) { Fail("engine: no rocket"); yield break; }
                    EngineBlock(r, b);
                    break;
                }

                case Kinds.Parachute:
                    yield return ParachuteBlock(b);
                    break;

                case Kinds.Deploy:
                {
                    Rocket r = WorldVars.GetPlayerRocket();
                    if (r == null) { Fail("deploy: no rocket"); yield break; }
                    DeployBlock(r, b);
                    break;
                }

                case Kinds.Wheel:
                    yield return WheelBlock(b);
                    break;

                case Kinds.Hold:
                    yield return HoldBlock(b);
                    break;

                case Kinds.Warp:
                    WarpBlock(b);
                    break;

                case Kinds.Call:
                    yield return CallBlock(b, ctx);
                    break;

                case Kinds.RcsMove:
                    yield return RcsMoveBlock(b);
                    break;

                case Kinds.Burn:
                    yield return BurnBlock(b);
                    break;

                case Kinds.Circularize:
                    yield return CircularizeBlock(b);
                    break;

                case Kinds.Match:
                    yield return MatchBlock(b);
                    break;

                case Kinds.Log:
                {
                    string msg = b.a ?? "";
                    string rendered = Interpolate(msg);
                    if (!IsRunning) yield break;
                    Print("LOG: " + rendered);
                    if (MsgDrawer.main != null) MsgDrawer.main.Log(rendered);
                    break;
                }

                case Kinds.Dialog:
                    yield return DialogBlock(b);
                    break;

                case Kinds.SetVar:
                {
                    string name = (b.a ?? "").Trim();
                    if (name.Length == 0) { Fail("set variable: empty name"); yield break; }
                    double v = Eval(b.b);
                    if (!IsRunning) yield break;
                    userVars[name] = v;
                    break;
                }

                case Kinds.If:
                {
                    bool c = Cond(b);
                    if (!IsRunning) yield break;
                    yield return RunList(c ? b.then : b.els, ctx);
                    break;
                }

                case Kinds.While:
                {
                    int guard = 0;
                    while (Cond(b))
                    {
                        if (!IsRunning) yield break;
                        yield return RunList(b.body, ctx);
                        if (!IsRunning) yield break;
                        if (ctx.breakFlag) { ctx.breakFlag = false; break; }
                        if (ctx.continueFlag) { ctx.continueFlag = false; yield return null; continue; }
                        yield return null;
                        if (++guard > 100000) { Fail("while: too many iterations (100000)"); yield break; }
                    }
                    break;
                }

                case Kinds.Repeat:
                {
                    double count = Eval(b.a);
                    if (!IsRunning) yield break;
                    int n = (int)Math.Min(Math.Floor(count), 1000000.0);
                    for (int i = 0; i < n; i++)
                    {
                        if (!IsRunning) yield break;
                        yield return RunList(b.body, ctx);
                        if (!IsRunning) yield break;
                        if (ctx.breakFlag) { ctx.breakFlag = false; break; }
                        if (ctx.continueFlag) { ctx.continueFlag = false; yield return null; continue; }
                        yield return null;
                    }
                    break;
                }

                case Kinds.Break:
                    ctx.breakFlag = true;
                    break;

                case Kinds.Continue:
                    ctx.continueFlag = true;
                    break;

                case Kinds.Toggle:
                {
                    string name = (b.a ?? "").Trim();
                    if (name.Length == 0) { Fail("toggle: variable name required"); yield break; }
                    double cur;
                    userVars.TryGetValue(name, out cur);
                    userVars[name] = cur > 0.5 ? 0.0 : 1.0;
                    break;
                }

                case Kinds.Random:
                {
                    string name = (b.a ?? "").Trim();
                    if (name.Length == 0) { Fail("random: variable name required"); yield break; }
                    userVars[name] = UnityEngine.Random.value;
                    break;
                }

                case Kinds.Fail:
                {
                    Fail(string.IsNullOrEmpty(b.a) ? "end failure block reached" : b.a);
                    yield break;
                }

                default:
                    Fail("unknown block kind '" + b.kind + "'");
                    yield break;
            }
        }

        // ---------------- 转向控制器 ----------------

        // 可选时长：b 为空 = 瞬时到位；否则在 b 秒内匀速转过去。
        private double OptionalDuration(Block b)
        {
            if (b == null || string.IsNullOrWhiteSpace(b.b)) return 0.0;
            double d = Eval(b.b);
            if (!IsRunning) return 0.0;
            return d > 0.0 ? d : 0.0;
        }

        // 姿态控制：按“推力方向”(Rocket.GetRotation) 计算误差，用 rb2d 增量旋转逼近目标。增量式修正会自动抵消发动机安装角，比绝对赋值可靠（ManeuverNode 的实测结论）。不用 turnAxis：既没 RCS 也没发动机时也能转，且不会把角速度甩出去。
        private IEnumerator TurnTo(Rocket r, double targetDeg, double duration)
        {
            r.arrowkeys.turnAxis.Value = 0f;

            // 时间加速时刚体不参与物理模拟，姿态无意义：等到恢复实时物理再转
            if (WorldTime.main != null)
            {
                bool warned = false;
                while (IsRunning && !WorldTime.main.realtimePhysics.Value)
                {
                    if (!warned)
                    {
                        Print("turn: waiting for realtime physics (leave timewarp)");
                        warned = true;
                    }
                    yield return null;
                }
            }
            if (!IsRunning) yield break;

            // GetRotation 是推力方向，rb2d.rotation 是刚体角度，两者差一个固定偏移
            double offset = WorldVars.NormalizeAngle(r.GetRotation() - r.rb2d.rotation);
            double bodyTarget = WorldVars.NormalizeAngle(targetDeg - offset);
            double initial = WorldVars.NormalizeAngle(bodyTarget - r.rb2d.rotation);
            double maxRate = duration > 0.05 ? Math.Abs(initial) / duration : 720.0;
            maxRate = Math.Max(45.0, maxRate);
            double deadline = Time.unscaledTime + Math.Max(30.0, duration * 2.0 + 10.0);
            float nextLog = 0f;

            while (IsRunning)
            {
                double err = WorldVars.NormalizeAngle(bodyTarget - r.rb2d.rotation);
                if (Math.Abs(err) <= 0.4)
                {
                    r.rb2d.angularVelocity = 0f;
                    r.arrowkeys.turnAxis.Value = 0f;
                    yield break;
                }

                if (Time.unscaledTime > deadline)
                {
                    Fail("turn: target not reached (" + err.ToString("0.0") + " deg off)");
                    yield break;
                }

                double step = Math.Min(Math.Abs(err), maxRate * Math.Max(1e-3, Time.deltaTime));
                r.rb2d.MoveRotation(r.rb2d.rotation + (float)(Math.Sign(err) * step));
                r.rb2d.angularVelocity = 0f;
                r.arrowkeys.turnAxis.Value = 0f;

                if (Time.unscaledTime >= nextLog)
                {
                    nextLog = Time.unscaledTime + 1f;
                    Print("TURN: target=" + targetDeg.ToString("0") + " body=" + bodyTarget.ToString("0")
                        + " cur=" + r.rb2d.rotation.ToString("0") + " err=" + err.ToString("0.0"));
                }

                yield return null;
            }
        }

        // ---------------- 分级 ----------------

        // 激活最下面一个有零件的分级（与游戏 StagingDrawer.UseStage 同一路径）。
        private void ActivateStage(Rocket r, int ordinal)
        {
            Stage target = StageByOrdinal(r, ordinal);
            if (target == null) { Fail("stage " + ordinal + ": no such stage"); return; }

            int identifier = ++stageIdentifier;
            target.useStageIdentifier = identifier;
            Part[] parts = target.parts.ToArray();
            UsePartData[] used = Rocket.UseParts(true, ToRegions(parts));

            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null) continue;
                if (used != null && i < used.Length && !used[i].successfullyUsedPart) continue;

                Staging staging = parts[i].Rocket.staging;
                for (int j = staging.stages.Count - 1; j >= 0; j--)
                {
                    Stage s = staging.stages[j];
                    if (s.useStageIdentifier == identifier)
                    {
                        s.RemovePart(parts[i], record: false);
                        if (target.PartCount == 0 && staging.stages.Contains(target))
                            staging.RemoveStage(target, record: false);
                    }
                }
            }
            Print("STAGE: activated stage " + target.stageId);
        }

        private Stage StageByOrdinal(Rocket r, int ordinal)
        {
            int n = 0;
            for (int i = 0; i < r.staging.stages.Count; i++)
            {
                Stage st = r.staging.stages[i];
                if (st == null || st.PartCount == 0) continue;
                n++;
                if (n == ordinal) return st;
            }
            return null;
        }

        // 编号顺序：先按分级（从下往上），同级内按模块顺序；1 = 最下面那级的第一台
        public static int[] OrderedModuleIndices<T>(Rocket r, T[] mods) where T : MonoBehaviour
        {
            if (mods == null || mods.Length == 0) return new int[0];
            List<Stage> stages = new List<Stage>();
            foreach (Stage s in r.staging.stages)
                if (s != null && s.PartCount > 0) stages.Add(s);

            int[] order = new int[mods.Length];
            int[] stageOf = new int[mods.Length];
            for (int i = 0; i < mods.Length; i++)
            {
                order[i] = i;
                stageOf[i] = int.MaxValue;
                Part part = mods[i] != null ? mods[i].GetComponent<Part>() : null;
                if (part == null) continue;
                for (int k = 0; k < stages.Count; k++)
                {
                    if (stages[k].parts.Contains(part)) { stageOf[i] = k; break; }
                }
            }

            Array.Sort(order, (a, b) =>
            {
                int c = stageOf[a].CompareTo(stageOf[b]);
                return c != 0 ? c : a.CompareTo(b);
            });
            return order;
        }

        // 选择器：空/all = 全部；数字 = 第 N 个；stage N = 第 N 级里的全部
        private int[] SelectModules<T>(Rocket r, string selector) where T : MonoBehaviour
        {
            T[] mods = r.partHolder.GetModules<T>();
            if (mods == null || mods.Length == 0) return new int[0];

            int[] ordered = OrderedModuleIndices(r, mods);
            string sel = (selector ?? "").Trim();

            if (sel.Length == 0 || sel.Equals("all", StringComparison.OrdinalIgnoreCase))
                return ordered;

            if (sel.StartsWith("stage", StringComparison.OrdinalIgnoreCase))
            {
                int ord;
                if (!int.TryParse(sel.Substring(5).Trim(), out ord)) return new int[0];
                Stage stage = StageByOrdinal(r, ord);
                if (stage == null) return new int[0];

                List<int> list = new List<int>();
                for (int i = 0; i < ordered.Length; i++)
                {
                    Part part = mods[ordered[i]] != null ? mods[ordered[i]].GetComponent<Part>() : null;
                    if (part != null && stage.parts.Contains(part)) list.Add(ordered[i]);
                }
                return list.ToArray();
            }

            int index;
            if (!int.TryParse(sel, out index) || index < 1 || index > ordered.Length) return new int[0];
            return new[] { ordered[index - 1] };
        }

        private void EngineBlock(Rocket r, Block b)
        {
            EngineModule[] engines = r.partHolder.GetModules<EngineModule>();
            int[] idx = SelectModules<EngineModule>(r, b.a);
            if (idx.Length == 0) { Print("engine: no match for '" + (b.a ?? "all") + "' (skipped)"); return; }

            bool on = !string.Equals(b.dir, "off", StringComparison.OrdinalIgnoreCase);
            for (int i = 0; i < idx.Length; i++)
                engines[idx[i]].engineOn.Value = on;

            Print("ENGINE: " + (b.a ?? "all") + " -> " + (on ? "on" : "off") + " (" + idx.Length + ")");
        }

        private IEnumerator ParachuteBlock(Block b)
        {
            Rocket r = WorldVars.GetPlayerRocket();
            if (r == null) { Fail("parachute: no rocket"); yield break; }

            ParachuteModule[] chutes = r.partHolder.GetModules<ParachuteModule>();
            int[] idx = SelectModules<ParachuteModule>(r, b.a);
            if (idx.Length == 0) { Print("parachute: no match for '" + (b.a ?? "all") + "' (skipped)"); yield break; }

            bool cut = string.Equals(b.dir, "cut", StringComparison.OrdinalIgnoreCase);
            for (int i = 0; i < idx.Length; i++)
            {
                ParachuteModule ch = chutes[idx[i]];
                if (cut)
                {
                    ch.targetState.Value = 3f;
                    ch.state.Value = 3f;
                }
                else ch.DeployParachute(new UsePartData(new UsePartData.SharedData(false), null));
            }

            if (!cut)
            {
                yield return new WaitForSeconds(0.8f);
                for (int i = 0; i < idx.Length; i++)
                    chutes[idx[i]].DeployParachute(new UsePartData(new UsePartData.SharedData(false), null));
            }

            Print("PARACHUTE: " + (b.a ?? "all") + " -> " + (cut ? "cut" : "deploy") + " (" + idx.Length + ")");
        }

        private double SpeedVar() { double v; return vars.TryGetValue("Speed", out v) ? v : 0.0; }

        private IEnumerator BurnBlock(Block b)
        {
            Rocket r = WorldVars.GetPlayerRocket();
            if (r == null) { Fail("burn: no rocket"); yield break; }
            yield return WaitRealtime();
            if (!IsRunning) yield break;

            double dv = Eval(string.IsNullOrEmpty(b.a) ? "0" : b.a);
            if (!IsRunning) yield break;
            if (dv <= 0.0) { Print("BURN: dv <= 0, skipped"); yield break; }

            bool retro = string.Equals(b.dir, "retrograde", StringComparison.OrdinalIgnoreCase);
            Location loc = r.location.Value;
            double angle = retro ? WorldVars.VelocityAngle(loc) + 180.0 : WorldVars.VelocityAngle(loc);
            yield return TurnTo(r, angle, 1.5);
            if (!IsRunning) yield break;

            double startSpeed = SpeedVar();
            double targetSpeed = retro ? Math.Max(0.0, startSpeed - dv) : startSpeed + dv;
            double maxTwr = vars.ContainsKey("MaxTWR") ? vars["MaxTWR"] : 1.0;
            double accel = Math.Max(0.2, maxTwr * 9.81);
            float timeout = (float)Math.Min(240.0, 15.0 + dv / accel * 4.0);
            float end = Time.time + timeout;

            r.throttle.throttlePercent.Value = 1f;
            r.throttle.throttleOn.Value = true;

            while (IsRunning && Time.time < end)
            {
                RefreshVars();
                double sp = SpeedVar();
                if (!retro && sp >= targetSpeed) break;
                if (retro && sp <= targetSpeed) break;
                yield return null;
            }

            r.throttle.throttlePercent.Value = 0f;
            Print("BURN: " + (retro ? "retro " : "pro ") + dv.ToString("0.#") + " m/s done");
        }

        private IEnumerator CircularizeBlock(Block b)
        {
            Rocket r = WorldVars.GetPlayerRocket();
            if (r == null) { Fail("circularize: no rocket"); yield break; }
            yield return WaitRealtime();
            if (!IsRunning) yield break;

            double tol = Eval(string.IsNullOrEmpty(b.a) ? "3000" : b.a);
            if (!IsRunning) yield break;
            if (tol <= 0.0) tol = 3000.0;

            // 没入轨就圆化等于原地满油门烧到 2 km/s，先拦下来
            RefreshVars();
            double apoNow = vars.ContainsKey("Apoapsis") ? vars["Apoapsis"] : 0.0;
            if (apoNow < 2000.0)
            {
                Fail("circularize: not in orbit (apoapsis " + apoNow.ToString("0") + " m < 2000 m)");
                yield break;
            }

            // 圆轨道速度 v = sqrt(mu / r)，mu = g * R^2；按当前速度决定顺/逆行
            for (int pass = 0; pass < 4 && IsRunning; pass++)
            {
                RefreshVars();
                double radius = vars.ContainsKey("Radius") ? vars["Radius"] : 0.0;
                double planetR = vars.ContainsKey("PlanetRadius") ? vars["PlanetRadius"] : 0.0;
                double g = vars.ContainsKey("Gravity") ? vars["Gravity"] : 0.0;
                if (radius <= 0.0 || planetR <= 0.0 || g <= 0.0) { Fail("circularize: no planet data"); yield break; }

                double vCirc = Math.Sqrt(g * planetR * planetR / radius);
                double speed = SpeedVar();
                double diff = vCirc - speed;

                Print("CIRC: pass " + (pass + 1) + " v=" + speed.ToString("0.0")
                    + " vCirc=" + vCirc.ToString("0.0") + " diff=" + diff.ToString("0.0"));

                if (Math.Abs(diff) <= Math.Max(0.5, tol / 1000.0)) break;

                bool prograde = diff > 0.0;
                Location loc = r.location.Value;
                double angle = prograde ? WorldVars.VelocityAngle(loc) : WorldVars.VelocityAngle(loc) + 180.0;
                yield return TurnTo(r, angle, 1.5);
                if (!IsRunning) yield break;

                r.throttle.throttlePercent.Value = 1f;
                r.throttle.throttleOn.Value = true;
                float end = Time.time + 60f;

                while (IsRunning && Time.time < end)
                {
                    RefreshVars();
                    double sp = SpeedVar();
                    if ((prograde && sp >= vCirc) || (!prograde && sp <= vCirc)) break;
                    yield return null;
                }

                r.throttle.throttlePercent.Value = 0f;
                if (!IsRunning) yield break;
            }
        }

        private IEnumerator MatchBlock(Block b)
        {
            Rocket r = WorldVars.GetPlayerRocket();
            if (r == null) { Fail("match: no rocket"); yield break; }
            yield return WaitRealtime();
            if (!IsRunning) yield break;

            SelectableObject target = WorldVars.GetTarget();
            if (target == null) { Fail("match: no target selected in map"); yield break; }

            double tol = Eval(string.IsNullOrEmpty(b.a) ? "1" : b.a);
            if (!IsRunning) yield break;
            if (tol <= 0.0) tol = 1.0;

            Location loc = r.location.Value;
            Location tl = target.Location;
            Double2 rel = tl.GetSolarSystemPosition(WorldTime.main != null ? WorldTime.main.worldTime : 0.0)
                - loc.GetSolarSystemPosition(WorldTime.main != null ? WorldTime.main.worldTime : 0.0);
            double relSpeed = tl.planet == loc.planet
                ? (tl.velocity - loc.velocity).magnitude
                : rel.magnitude;

            if (relSpeed <= tol) { Print("MATCH: already matched"); yield break; }

            double angle = Math.Atan2(-(tl.velocity - loc.velocity).x, -(tl.velocity - loc.velocity).y) * 180.0 / Math.PI;
            yield return TurnTo(r, angle, 1.5);
            if (!IsRunning) yield break;

            r.throttle.throttlePercent.Value = 1f;
            r.throttle.throttleOn.Value = true;
            float end = Time.time + 60f;

            while (IsRunning && Time.time < end)
            {
                RefreshVars();
                loc = r.location.Value;
                Double2 relNow = tl.velocity - loc.velocity;
                if (relNow.magnitude <= tol) break;
                yield return null;
            }

            r.throttle.throttlePercent.Value = 0f;
            Print("MATCH: done");
        }

        private void LateUpdate()
        {
            if (!IsRunning) return;
            Rocket r = WorldVars.GetPlayerRocket();
            if (r == null) return;

            if (Time.time < wheelDriveUntil) r.arrowkeys.turnAxis.Value = wheelDrive;

            if (Time.time < rcsUntil)
            {
                r.arrowkeys.horizontalAxis.Value = rcsDrive;
                r.arrowkeys.verticalAxis.Value = Vector2.zero;
            }
        }

        public void NotifyCrash(Rocket rocket, string reason)
        {
            // 只认玩家这艘船；别的残骸解体不触发
            Rocket current = WorldVars.GetPlayerRocket();
            if (rocket != null && current != null && rocket != current) return;

            crashSerial++;
            Print("EVENT: crash (" + reason + ")");
        }

        private IEnumerator RcsMoveBlock(Block b)
        {
            Rocket r = WorldVars.GetPlayerRocket();
            if (r == null) { Fail("rcs: no rocket"); yield break; }

            float duration = 2f;
            if (!string.IsNullOrEmpty(b.a)) float.TryParse(b.a, out duration);
            if (duration <= 0f) duration = 2f;

            // 用刚体旋转直接算世界方向，别依赖 transform（根节点不转）
            float rad = (r.rb2d != null ? r.rb2d.rotation : 0f) * Mathf.Deg2Rad;
            Vector2 up = new Vector2(-Mathf.Sin(rad), Mathf.Cos(rad));
            Vector2 right = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

            string mode = (b.dir ?? "up").ToLowerInvariant();
            Vector2 dir;
            if (mode == "down") dir = -up;
            else if (mode == "right") dir = right;
            else if (mode == "left") dir = -right;
            else dir = up;

            Print("RCS dir " + mode + " = (" + dir.x.ToString("0.00") + "," + dir.y.ToString("0.00")
                + ") rot=" + (r.rb2d != null ? r.rb2d.rotation.ToString("0.#") : "?"));

            r.arrowkeys.rcs.Value = true;
            rcsDrive = dir;
            rcsUntil = Time.time + duration;

            float end = Time.time + duration;
            while (IsRunning && Time.time < end) yield return null;

            rcsUntil = 0f;
            rcsDrive = Vector2.zero;
            r.arrowkeys.horizontalAxis.Value = Vector2.zero;
            r.arrowkeys.verticalAxis.Value = Vector2.zero;
            r.arrowkeys.rcs.Value = false;
            Print("RCS: " + mode + " for " + duration.ToString("0.#") + "s");
        }

        private IEnumerator CallBlock(Block b, RunContext ctx)
        {
            string name = (b.a ?? "").Trim();
            FunctionDef f;
            if (!functions.TryGetValue(name, out f))
            {
                Fail("call: function '" + name + "' not found");
                yield break;
            }
            if (callDepth >= 32)
            {
                Fail("call: recursion too deep (32)");
                yield break;
            }

            callDepth++;
            yield return RunList(f.blocks ?? new List<Block>(), ctx);
            callDepth--;
            if (!IsRunning) yield break;
        }

        private void WarpBlock(Block b)
        {
            if (WorldTime.main == null) { Fail("warp: no world time"); return; }

            int target = 0;
            if (!string.IsNullOrEmpty(b.a)) int.TryParse(b.a.Trim(), out target);

            if (target <= 0)
            {
                WorldTime.main.StopTimewarp(false);
                Print("WARP: off");
                return;
            }

            int guard = 0;
            while (IsRunning && WorldTime.main.timewarpIndex < target && guard++ < 20)
            {
                int before = WorldTime.main.timewarpIndex;
                WorldTime.main.AccelerateTime();
                if (WorldTime.main.timewarpIndex == before) break;
            }
            while (IsRunning && WorldTime.main.timewarpIndex > target && guard++ < 40)
            {
                int before = WorldTime.main.timewarpIndex;
                WorldTime.main.DecelerateTime();
                if (WorldTime.main.timewarpIndex == before) break;
            }

            Print("WARP: index " + WorldTime.main.timewarpIndex + " (target " + target + ")");
        }

        private IEnumerator WaitRealtime()
        {
            bool warned = false;
            while (IsRunning && WorldTime.main != null && !WorldTime.main.realtimePhysics.Value)
            {
                if (!warned) { Print("waiting for realtime physics (exit timewarp)"); warned = true; }
                yield return null;
            }
        }

        private IEnumerator HoldBlock(Block b)
        {
            Rocket r = WorldVars.GetPlayerRocket();
            if (r == null) { Fail("hold: no rocket"); yield break; }
            yield return WaitRealtime();
            if (!IsRunning) yield break;

            bool altitudeMode = string.Equals(b.dir, "altitude", StringComparison.OrdinalIgnoreCase);
            double target = Eval(string.IsNullOrEmpty(b.a) ? "0" : b.a);
            if (!IsRunning) yield break;

            float duration = 10f;
            if (!string.IsNullOrEmpty(b.b)) float.TryParse(b.b, out duration);
            if (duration <= 0f) duration = 10f;

            // 垂直速度环：误差 m/s -> 油门修正；前馈用 1/MaxTWR 估算悬停油门
            double kp = 0.05, ki = 0.0015, kd = 0.02;
            double integral = 0.0, prev = 0.0;
            bool first = true;
            float nextHoldLog = 0f;
            float end = Time.time + duration;

            while (IsRunning && Time.time < end)
            {
                RefreshVars();
                double dt = Time.deltaTime;
                if (dt <= 0.0) { yield return null; continue; }

                // 发动机被关掉时 MaxTWR 会读成 0，先强制点火，否则 PID 永远起不来
                r.throttle.throttleOn.Value = true;

                double agl = vars.ContainsKey("AltitudeAGL") ? vars["AltitudeAGL"] : 0.0;
                double vs = vars.ContainsKey("VerticalSpeed") ? vars["VerticalSpeed"] : 0.0;
                double maxTwr = vars.ContainsKey("MaxTWR") ? vars["MaxTWR"] : 1.0;
                if (maxTwr < 0.05) maxTwr = 0.7;

                double targetVs = target;
                if (altitudeMode)
                {
                    double altErr = target - agl;
                    targetVs = Math.Max(-20.0, Math.Min(20.0, altErr * 0.3));
                }

                double err = targetVs - vs;
                if (first) { prev = vs; first = false; }

                bool warping = WorldTime.main != null && WorldTime.main.timewarpIndex > 0;
                if (!warping)
                {
                    integral += err * dt;
                    integral = Math.Max(-10.0, Math.Min(10.0, integral));
                }

                double deriv = -(vs - prev) / dt;
                prev = vs;

                double ff = maxTwr > 0.05 ? 1.0 / maxTwr : 0.0;
                double u = ff + kp * err + ki * integral + kd * deriv;
                u = Math.Max(0.0, Math.Min(1.0, u));

                r.throttle.throttlePercent.Value = (float)u;
                if (u > 0.001) r.throttle.throttleOn.Value = true;

                // 每 2 秒打一次，低 TWR 时能看出是物理不够还是控制没输出
                if (Time.time >= nextHoldLog)
                {
                    nextHoldLog = Time.time + 2f;
                    Print("HOLD t=" + (Time.time - (end - duration)).ToString("0.0")
                        + " agl=" + agl.ToString("0") + " vs=" + vs.ToString("0.0")
                        + " u=" + u.ToString("0.00") + " maxTwr=" + maxTwr.ToString("0.00"));
                }

                yield return null;
            }

            if (r != null) r.throttle.throttlePercent.Value = 0f;
            Print("HOLD: " + (altitudeMode ? "altitude " : "vspeed ") + target.ToString("0.#") + " done");
        }

        private IEnumerator WheelBlock(Block b)
        {
            Rocket r = WorldVars.GetPlayerRocket();
            if (r == null) { Fail("wheel: no rocket"); yield break; }

            WheelModule[] wheels = r.partHolder.GetModules<WheelModule>();
            int[] idx = SelectModules<WheelModule>(r, b.a);
            if (idx.Length == 0) { Print("wheel: no match for '" + (b.a ?? "all") + "' (skipped)"); yield break; }

            string mode = (b.dir ?? "on").ToLowerInvariant();
            if (mode == "on" || mode == "off")
            {
                bool on = mode == "on";
                for (int i = 0; i < idx.Length; i++) wheels[idx[i]].on.Value = on;
                Print("WHEEL: " + (b.a ?? "all") + " -> " + mode + " (" + idx.Length + ")");
                yield break;
            }

            double v = Eval(string.IsNullOrEmpty(b.b) ? "0" : b.b);
            if (!IsRunning) yield break;

            // power/steer 都是驱动：Q/E 轴每帧被输入覆盖，所以用 LateUpdate 抢在物理步之前写
            float duration = 2f;
            if (!string.IsNullOrEmpty(b.title)) float.TryParse(b.title, out duration);
            if (duration <= 0f) duration = 2f;

            for (int i = 0; i < idx.Length; i++) wheels[idx[i]].on.Value = true;
            wheelDrive = (float)v;
            wheelDriveUntil = Time.time + duration;

            float end = Time.time + duration;
            while (IsRunning && Time.time < end) yield return null;

            wheelDrive = 0f;
            wheelDriveUntil = 0f;
            if (r != null) r.arrowkeys.turnAxis.Value = 0f;
            Print("WHEEL: drive " + v.ToString("0.##") + " for " + duration.ToString("0.#") + "s (" + idx.Length + ")");
        }

        private void DeployBlock(Rocket r, Block b)
        {
            ToggleModule[] mods = r.partHolder.GetModules<ToggleModule>();
            int[] idx = SelectModules<ToggleModule>(r, b.a);
            if (idx.Length == 0) { Print("deploy: no match for '" + (b.a ?? "all") + "' (skipped)"); return; }

            bool deploy = !string.Equals(b.dir, "retract", StringComparison.OrdinalIgnoreCase);
            for (int i = 0; i < idx.Length; i++)
            {
                ToggleModule m = mods[idx[i]];
                if (m.state != null) m.state.SetTargetTime(deploy ? 1f : 0f);
            }
            Print("DEPLOY: " + (b.a ?? "all") + " -> " + (deploy ? "deploy" : "retract") + " (" + idx.Length + ")");
        }

        private static (Part, PolygonData)[] ToRegions(Part[] parts)
        {
            var regions = new (Part, PolygonData)[parts.Length];
            for (int i = 0; i < parts.Length; i++) regions[i] = (parts[i], null);
            return regions;
        }

        // ---------------- 条件 / 表达式 ----------------

        private bool Cond(Block b)
        {
            double left = Eval(b.a);
            if (!IsRunning) return false;
            double right = Eval(b.b);
            if (!IsRunning) return false;

            switch (b.op)
            {
                case "<": return left < right;
                case "<=": return left <= right;
                case ">": return left > right;
                case ">=": return left >= right;
                case "==": return Math.Abs(left - right) <= 1e-9 + 1e-9 * Math.Abs(left);
                case "!=": return Math.Abs(left - right) > 1e-9 + 1e-9 * Math.Abs(left);
                default:
                    Fail("unknown operator '" + b.op + "'");
                    return false;
            }
        }

        // log 文本里的 {表达式} 替换成数值；{planet} 替换成当前天体名。未知变量按求值错误处理。
        // 弹窗/数字输入：等用户点 OK/Cancel 或超时
        private IEnumerator DialogBlock(Block b)
        {
            if (DialogUI.main == null)
            {
                Print("DIALOG: UI not available");
                yield break;
            }

            string title = Interpolate(b.title ?? "");
            string body = Interpolate(b.a ?? "");
            bool numeric = string.Equals(b.dir, "number", StringComparison.OrdinalIgnoreCase);

            float timeout = 0f;
            if (!string.IsNullOrEmpty(b.b))
            {
                double t = Eval(b.b);
                if (!IsRunning) yield break;
                if (t > 0.0) timeout = (float)t;
            }

            Print("DIALOG: " + title);
            DialogUI.main.Open(title, body, numeric, timeout);
            while (IsRunning && DialogUI.main.IsOpen) yield return null;
            if (!IsRunning) yield break;

            double value;
            if (numeric && DialogUI.main.TryTake(out value))
            {
                string name = string.IsNullOrEmpty(b.var) ? "input" : b.var;
                userVars[name] = value;
                Print("INPUT: " + name + " = " + value.ToString("0.###", CultureInfo.InvariantCulture));
            }
        }

        private string Interpolate(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text;
            return Placeholder.Replace(text, match =>
            {
                string expr = match.Groups[1].Value.Trim();
                if (expr.Equals("planet", StringComparison.OrdinalIgnoreCase))
                {
                    Rocket r = WorldVars.GetPlayerRocket();
                    if (r == null) return "?";
                    try { return (string)r.location.planet.Value.DisplayName; }
                    catch (Exception) { return "?"; }
                }
                double v = Eval(expr);
                if (!IsRunning) return match.Value;
                return v.ToString("0.###", CultureInfo.InvariantCulture);
            });
        }

        // 求值。出错不抛异常：打印错误并停机（协程里不能跨 yield try/catch）。
        private double Eval(string expr)
        {
            try
            {
                RefreshVars();
                double v = Expressions.Eval(expr, vars);
                if (!double.IsFinite(v))
                {
                    Fail("expression '" + expr + "' is not finite");
                    return double.NaN;
                }
                return v;
            }
            catch (Exception e)
            {
                Fail(e.Message);
                return double.NaN;
            }
        }

        private void RefreshVars()
        {
            vars.Clear();
            WorldVars.Fill(WorldVars.GetPlayerRocket(), vars);
            foreach (KeyValuePair<string, double> kv in userVars)
                vars[kv.Key] = kv.Value;
        }

        private void CheckRocket()
        {
            Rocket r = WorldVars.GetPlayerRocket();
            if (r != null)
            {
                hadRocket = true;
                return;
            }
            // 程序启动时可能有火箭，飞着飞着炸了 → 停机
            if (hadRocket) Finish("Rocket lost");
        }

        private static double GameTime()
        {
            // 用游戏时间：时间加速时 Wait 会被一起推进
            return WorldTime.main != null ? WorldTime.main.worldTime : (double)Time.time;
        }
    }
}
