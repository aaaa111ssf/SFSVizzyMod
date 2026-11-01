using System.Collections.Generic;
using Newtonsoft.Json;
using SFS.IO;

namespace SFSVizzyMod
{
    // 积木种类常量（JSON 里存的 kind 字符串）。
    internal static class Kinds
    {
        public const string Wait = "wait";              // a = 时长表达式（秒，游戏时间）
        public const string WaitUntil = "wait_until";   // a/op/b = 条件，条件为真才继续
        public const string SetThrottle = "set_throttle"; // a = 油门表达式 (0..1)
        public const string ThrottleOn = "throttle_on";
        public const string ThrottleOff = "throttle_off";
        public const string RcsOn = "rcs_on";
        public const string RcsOff = "rcs_off";
        public const string Turn = "turn";              // a = 角度(度)，dir = left/right/rel，b = 可选时长(秒)
        public const string TurnTo = "turn_to";         // a = 角度(度，相对天顶)，dir = deg/up/down/prograde/retrograde，b = 可选时长(秒)
        public const string Stage = "stage";            // 激活下一级，或第 N 级
        public const string SetVar = "set_var";         // a = 变量名, b = 表达式
        public const string Log = "log";                // a = 文本，支持 {表达式} 插值
        public const string Engine = "engine";          // dir = on/off，a = all / 序号 / stage N
        public const string Parachute = "parachute";
        public const string Deploy = "deploy";
        public const string Wheel = "wheel";
        public const string Hold = "hold";
        public const string Warp = "warp";
        public const string Call = "call";
        public const string Continue = "continue";
        public const string Toggle = "toggle";
        public const string Random = "random";
        public const string RcsMove = "rcs_move";
        public const string Burn = "burn";               // a = dv，dir = prograde/retrograde
        public const string Circularize = "circularize"; // a = 容差米
        public const string Match = "match";             // a = 容差 m/s        // dir = up/down/left/right，a = 秒              // a = 函数名              // a = 目标档位，0/空 = 关闭              // dir = vspeed/altitude，a = 目标，b = 秒            // dir = on/off/power/steer，a = 选择器，b = 值，title = 秒          // dir = deploy/retract，a = all / 序号 / stage N    // dir = deploy/cut，a = all / 序号 / stage N
        public const string Dialog = "dialog";          // title/a = 文本，dir = message/number，var = 变量名，b = 超时秒
        public const string If = "if";                  // a/op/b = 条件, then/els = 分支
        public const string While = "while";            // a/op/b = 条件, body = 循环体
        public const string Repeat = "repeat";          // a = 次数表达式, body = 循环体
        public const string Break = "break";            // 跳出最近的 while/repeat
        public const string Fail = "fail";              // a = 失败信息，终止程序
    }

    // 一块积木。if/while/repeat 带 then/els/body 子容器，其余为空。
    public class Block
    {
        public int id;            // 浏览器里的积木编号，用来定位错误
        public string kind = Kinds.Wait;
        public string a = "";        // 主表达式 / 文本 / 变量名
        public string op = "<=";     // 条件运算符（仅 if/while/wait_until）
        public string b = "";        // 条件右表达式（仅 if/while/wait_until / set_var 的值 / turn 的可选时长）
        public string dir;           // 方向预设: turn = left/right/rel；turn_to = deg/up/down/prograde/retrograde
        public string title = "";    // dialog 标题
        public string var = "";      // dialog 数字输入写回哪个变量
        public List<Block> then = new List<Block>();
        public List<Block> els = new List<Block>();
        public List<Block> body = new List<Block>();
    }

    // 一个 ON START 事件（Vizzy 支持多个，彼此并行执行）。
    public class LaunchEvent
    {
        public List<Block> blocks = new List<Block>();
    }

    // 高度触发事件：程序运行期间每帧采样高度，条件从 false 变为 true 时执行 blocks。mode = agl（真实离地高度，按行星极坐标的径向地形计算，水面算地面）或 asl（海拔）。once = true 只触发一次；false 则离开阈值（2 m 回差）后可再次触发。
    public class Trigger
    {
        public string mode = "agl";
        public string op = ">=";
        public string value = "10000";   // 表达式，程序启动时求值一次
        public bool once = true;
        public List<Block> blocks = new List<Block>();
    }

    // 整个程序：多个 ON START + 高度触发事件 + 变量初值。
    public class FunctionDef
    {
        public string name = "";
        public List<Block> blocks = new List<Block>();
    }

    public class GameEvent
    {
        public string type = "key";   // key / stage / landed / dock
        public string key = "R";
        public bool once = true;
        public List<Block> blocks = new List<Block>();
    }

    public class ProgramData
    {
        // 旧版单入口（v1 存档）。新程序写 launches，main 仅用于兼容读取。
        public List<Block> main = new List<Block>();

        public List<LaunchEvent> launches = new List<LaunchEvent>();
        public List<Trigger> triggers = new List<Trigger>();
        public List<GameEvent> events = new List<GameEvent>();
        public List<FunctionDef> functions = new List<FunctionDef>();
        public Dictionary<string, double> variables = new Dictionary<string, double>();

        // 取实际要执行的入口列表；没有 launches 的老存档回退成单个入口。
        public List<LaunchEvent> GetLaunches()
        {
            if (launches != null && launches.Count > 0) return launches;
            List<LaunchEvent> fallback = new List<LaunchEvent>();
            fallback.Add(new LaunchEvent { blocks = main ?? new List<Block>() });
            return fallback;
        }
    }

    public static class ProgramIO
    {
        static string FilePath()
        {
            // BasePath.ToString() 即真实路径；Mods 目录由游戏自己决定
            string mods = FileLocations.ModsFolder.ToString();
            string dir = System.IO.Path.Combine(mods, "SFSVizzyMod");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "program.json");
        }

        // 旧模组名（SFSBlocks）的存档位置，用于一次性迁移。
        static string LegacyPath()
        {
            try
            {
                string mods = FileLocations.ModsFolder.ToString();
                return System.IO.Path.Combine(mods, "SFSBlocks", "program.json");
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        // 新位置优先；没有就读旧位置。
        static string ExistingPath()
        {
            try
            {
                string path = FilePath();
                if (System.IO.File.Exists(path)) return path;
                string legacy = LegacyPath();
                if (legacy != null && System.IO.File.Exists(legacy)) return legacy;
            }
            catch (System.Exception) { }
            return null;
        }

        static JsonSerializerSettings Settings => new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented
        };

        public static void Save(ProgramData p)
        {
            System.IO.File.WriteAllText(FilePath(), JsonConvert.SerializeObject(p, Settings));
        }

        public static ProgramData Load()
        {
            string path = ExistingPath();
            if (path == null) return null;

            ProgramData data = JsonConvert.DeserializeObject<ProgramData>(System.IO.File.ReadAllText(path));
            if (data != null && path != FilePath())
            {
                // 旧目录的存档 → 自动写到新目录（迁移一次即可）
                try { Save(data); }
                catch (System.Exception) { }
            }
            return data;
        }

        // 原样返回磁盘上的 JSON（给浏览器 GET /api/program 用）。
        public static string LoadRaw()
        {
            try
            {
                string path = ExistingPath();
                if (path == null) return "{\"launches\":[{\"blocks\":[]}],\"triggers\":[],\"variables\":{}}";
                return System.IO.File.ReadAllText(path);
            }
            catch (System.Exception)
            {
                return "{\"launches\":[{\"blocks\":[]}],\"triggers\":[],\"variables\":{}}";
            }
        }

        public static bool Exists()
        {
            return ExistingPath() != null;
        }
    }
}
