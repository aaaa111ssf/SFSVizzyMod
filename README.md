# SFS Vizzy

Block programming for Spaceflight Simulator 1.6. Press F8, the editor opens in your browser, build a program from blocks and run it in flight.

🇬🇧 **English** | [🇨🇳 中文](#中文)

---

## English

### How it works

Three parts, nothing exotic:

- The mod starts a local HTTP + WebSocket server on `127.0.0.1:8765` and serves one HTML file embedded in the dll. No download, no external dependency.
- The editor sends the program as JSON. A coroutine interpreter in the game walks the block tree one frame at a time and calls the game API: throttle, RCS, staging, engines, wheels, time warp.
- Telemetry comes back over the same socket about ten times a second, so the page shows live altitude, speed and apsides.

### Highlights

- **Browser editor** — real keyboard, a code view that converts both ways with the blocks, block search, drag with snap to the nearest slot, light and dark theme.
- **Not just on/off switches** — `hold altitude` and `hold vspeed` are PID loops with hover feedforward, `burn dv` stops on speed change, `warp` goes through the game's own time warp checks.
- **Parts, not only engines** — landing legs and solar panels (`deploy`), wheels with drive and steering, RCS translation, parachutes.
- **Errors point at the block** — a failed program shows a red bar and outlines the block that broke, instead of one line of text.
- **Two builds** — English and Chinese UI, same behaviour, install one.

### Install

Put `SFSVizzyMod.dll` into `Mods/SFSVizzyMod/` (the folder name must match the dll name) and restart the game.

The Chinese build is a separate package: `SFSVizzyMod-CN.dll` goes into `Mods/SFSVizzyMod-CN/`. **Install only one of them** — they share the same hotkeys and port.

### Use

1. Press **F8** in game and the editor opens in your browser. You can also open `http://127.0.0.1:8765/` yourself
2. Drag blocks onto the canvas, or switch to the CODE view and type
3. Hit Run. **R** runs in game, **P** stops

Programs are saved to `Mods/SFSVizzyMod/program.json` and loaded back next time.

### Blocks

| Group | Block | What it does |
| --- | --- | --- |
| Events | `on start` | Program entry point |
| Events | `on altitude agl >= 500 once` | Fires when you pass 500 m. Drop `once` to fire every time |
| Events | `on key "K"` | Fires on a key press |
| Events | `on stage` `on landed` `on dock` `on crash` | Stage / landed / docked / crashed |
| Flight | `wait 2` | Wait 2 seconds of game time |
| Flight | `wait until AltitudeAGL > 100` | Wait until a condition is true |
| Flight | `set throttle to 0.8` | Throttle, 0 to 1 |
| Flight | `throttle on` `throttle off` | Switch the engine without changing the throttle value |
| Flight | `rcs on` `rcs off` | RCS on and off |
| Flight | `rcs up 2` | Translate along the nose for 2 s; also `down` `left` `right` |
| Flight | `turn to 90 in 2` | Turn to 90° within 2 s. 0 is straight up, 90 is horizontal. Also takes `up` `down` `prograde` `retrograde` `target` |
| Flight | `turn left 30 in 1` | Turn 30° relative; also `right` `rel` |
| Flight | `stage` `stage 2` | Fire a stage; without a number it fires the next one |
| Parts | `engine all on` | Engine on and off. Selector takes `all`, an index, or `stage 2` |
| Parts | `parachute all deploy` | Deploy the chute (half then full); `cut` releases it |
| Parts | `deploy all` `retract all` | Fold and unfold parts: landing legs, solar panels |
| Parts | `wheel all on` | Wheels on and off |
| Parts | `wheel all power 0.4 5` | Wheel power 0.4 for 5 s |
| Parts | `wheel all steer 0.6 2` | Steer 0.6 for 2 s |
| Control | `hold vspeed 0 10` | PID hold on vertical speed; `0` hovers. Runs 10 s |
| Control | `hold altitude 200 10` | PID hold 200 m above ground |
| Control | `warp 3` | Time warp to step 3. `warp 0` turns it off |
| Guidance | `burn dv 50 prograde` | Burn 50 m/s prograde and stop by itself. `retrograde` burns the other way |
| Logic | `set x = 1 + 2` | Assign. Comparisons return 0/1, and `and` / `or` / `not` work across them. Functions: `min max abs sqrt clamp pow floor ceil round sin cos tan log ln`, constants `pi` `e` |
| Logic | `log "alt {AltitudeAGL} m"` | Print to the console; `{name}` is replaced with the value |
| Logic | `if … else … end` | Condition |
| Logic | `while … end` `repeat 3 … end` | Loops |
| Logic | `break` `continue` | Leave the loop / skip the rest of this pass |
| Logic | `toggle flag` | Flips 0 and 1 |
| Logic | `random r` | Writes a random 0 to 1 |
| Logic | `fail "out of fuel"` | Aborts with an error |
| Functions | `func name … end` | Define a function; variables are global |
| Functions | `call name` | Call it. Nesting works, up to 32 levels |
| UI | `popup "title" "text" timeout 5` | In-game popup, closes after 5 s |
| UI | `input "title" "enter a number" -> answer` | Number input, result goes into the variable |

### Variables

| Group | Names |
| --- | --- |
| Flight | `Speed` `VerticalSpeed` `HorizontalSpeed` `Altitude` `AltitudeAGL` `Heading` `AngularVel` |
| Rocket | `Mass` `TWR` `MaxTWR` `Fuel` `FuelAmount` `FuelCapacity` |
| Orbit | `Apoapsis` `Periapsis` `Eccentricity` `Escaping` `Radius` `PlanetRadius` |
| Environment | `Gravity` `AirDensity` `DynamicPressure` `AtmosphereTop` |
| Target | `TargetDistance` `TargetBearing` `TargetLongitude` `HasTarget` |

Apsides are in metres, speeds in m/s, `Fuel` is a 0 to 1 fraction.

### Examples

Everything in `examples/` can be pasted straight into the CODE view:

| File | What it is |
| --- | --- |
| `demo1_launch_to_orbit.txt` | Launch to orbit: pitch over, raise the apoapsis, circularize |
| `demo2_hover_hold.txt` | Hover for 5 s, then hold 200 m |
| `demo3_rover.txt` | Rover drives, steers and parks |
| `demo4_landing.txt` | Legs out, slow descent, chute, soft touchdown |
| `demo5_orbit_cruise.txt` | Time warp, prograde and retrograde burns |
| `demo6_functions_events.txt` | Functions, events, loops, random |
| `demo7_target_nav.txt` | Turn to the map target and log the distance |
| `demo8_error_demo.txt` | Fails on purpose to show the red error bar |
| `mechjeb.txt` | Six-mode autopilot: launch / circularize / apsides / land / chute / nav |
| `landing.txt` | Hand written proportional landing, good for learning how the PID works |

### Gotchas

- `wait`, `if` and `while` conditions can chain: `while tries < 12 and abs(Apoapsis - Ap) > tol`
- On the pad there is no orbit yet, so `Apoapsis` falls back to the current altitude
- `hold` and `burn` wait for time warp to end; burning while warping does nothing
- In the Chinese build the UI is Chinese, but code keywords and variables stay English
- When picking parts by index, press **H** in game to see the numbers

---

## 中文

Spaceflight Simulator 1.6 的积木编程模组 按 F8 在浏览器里打开编辑器 拖积木写程序 边飞边跑

🇬🇧 [English](#english) | 🇨🇳 **中文**

### 怎么实现的

三块 没什么玄的：

- 模组在 `127.0.0.1:8765` 起一个本地 HTTP + WebSocket 服务 页面是一整个 HTML 文件 直接嵌在 dll 里
- 编辑器把程序打包成 JSON 发过去 游戏里一个协程解释器逐帧走积木树 调用游戏自己的接口：油门、RCS、分级、发动机、轮子、时间加速
- 遥测从同一个 socket 往回推 大约每秒十次 页面上的高度、速度、拱点都是实时的

### 亮点

- **浏览器编辑器** —— 能用真键盘 代码视图和积木双向转换 能搜积木 拖动时自动吸到最近的插入点 明暗两套主题
- **不只是开关量** —— `hold altitude` 和 `hold vspeed` 是带悬停前馈的 PID `burn dv` 按速度差停火 `warp` 走游戏自己的加速检查
- **部件不止发动机** —— 着陆腿和太阳能板能展开收起 轮子能驱动和转向 RCS 能平移 降落伞能开能切
- **报错能定位** —— 程序失败时顶部弹红色错误条 同时把出错那块积木描红并滚到眼前 不是只丢一行字
- **两个版本** —— 英文版和中文版界面 行为一样 装一个

### 安装

把 `SFSVizzyMod.dll` 放进游戏目录的 `Mods/SFSVizzyMod/` 重启游戏 文件夹名要和 dll 名一样

中文版是另一个包：`SFSVizzyMod-CN.dll` 放 `Mods/SFSVizzyMod-CN/` 但 **两个版本只能装一个** 键位和端口会打架

### 用法

1. 游戏里按 **F8** 后浏览器会打开编辑器 也可以自己访问 `http://127.0.0.1:8765/`
2. 左边拖积木到画布 或者切到「代码」直接打字
3. 点「运行」 游戏里按 **R** 也能跑 按 **P** 停

程序存在 `Mods/SFSVizzyMod/program.json` 下次打开自动读回来

### 积木

| 分组 | 积木 | 干什么 |
| --- | --- | --- |
| 事件 | `on start` | 程序开始 |
| 事件 | `on altitude agl >= 500 once` | 飞到 500 m 时触发一次 去掉 `once` 就会反复触发 |
| 事件 | `on key "K"` | 按键触发 |
| 事件 | `on stage` `on landed` `on dock` `on crash` | 分级 / 着陆 / 对接 / 坠毁 |
| 飞行 | `wait 2` | 等 2 秒（游戏时间） |
| 飞行 | `wait until AltitudeAGL > 100` | 等到条件成立 |
| 飞行 | `set throttle to 0.8` | 油门 0~1 |
| 飞行 | `throttle on` `throttle off` | 只开关发动机 不改油门数值 |
| 飞行 | `rcs on` `rcs off` | RCS 开关 |
| 飞行 | `rcs up 2` | 沿机头方向平移 2 秒 可换 `down` `left` `right` |
| 飞行 | `turn to 90 in 2` | 2 秒内转到 90° 其中 0 是竖直 90 是水平 也能写 `up` `down` `prograde` `retrograde` `target` |
| 飞行 | `turn left 30 in 1` | 相对转 30° 可换 `right` `rel` |
| 飞行 | `stage` `stage 2` | 分级 不带数字就分下一级 |
| 部件 | `engine all on` | 发动机开关 选择器能写 `all`、编号、`stage 2` |
| 部件 | `parachute all deploy` | 开伞（先半开再全开） `cut` 是切断 |
| 部件 | `deploy all` `retract all` | 展开 / 收起折叠件 着陆腿和太阳能板都归它管 |
| 部件 | `wheel all on` | 轮子开关 |
| 部件 | `wheel all power 0.4 5` | 轮子动力 0.4 开 5 秒 |
| 部件 | `wheel all steer 0.6 2` | 转向 0.6 持续 2 秒 |
| 控制 | `hold vspeed 0 10` | PID 保持垂直速度 `0` 就是悬停 持续 10 秒 |
| 控制 | `hold altitude 200 10` | PID 保持离地 200 m |
| 控制 | `warp 3` | 时间加速到第 3 档 `warp 0` 关掉 |
| 制导 | `burn dv 50 prograde` | 顺行点火 50 m/s 够了自己停 `retrograde` 是反着烧 |
| 逻辑 | `set x = 1 + 2` | 赋值 比较返回 0/1 比较之间能接 `and` / `or` / `not` 函数有 `min max abs sqrt clamp pow floor ceil round sin cos tan log ln` 常量 `pi` `e` |
| 逻辑 | `log "高度 {AltitudeAGL} m"` | 打到控制台 `{变量}` 会换成当前值 |
| 逻辑 | `if … else … end` | 条件 |
| 逻辑 | `while … end` `repeat 3 … end` | 循环 |
| 逻辑 | `break` `continue` | 跳出循环 / 跳过本轮剩下的 |
| 逻辑 | `toggle flag` | 0 变 1、1 变 0 |
| 逻辑 | `random r` | 写一个 0~1 的随机数 |
| 逻辑 | `fail "没油了"` | 当场中止并报错 |
| 函数 | `func 名字 … end` | 定义函数 变量是全局的 |
| 函数 | `call 名字` | 调用 能套娃 但最多 32 层 |
| 界面 | `popup "标题" "正文" timeout 5` | 游戏内弹窗 5 秒自动关 |
| 界面 | `input "标题" "输入数字" -> 答案` | 弹输入框 结果存进变量 |

### 变量

| 分类 | 变量 |
| --- | --- |
| 飞行 | `Speed` `VerticalSpeed` `HorizontalSpeed` `Altitude` `AltitudeAGL` `Heading` `AngularVel` |
| 火箭 | `Mass` `TWR` `MaxTWR` `Fuel` `FuelAmount` `FuelCapacity` |
| 轨道 | `Apoapsis` `Periapsis` `Eccentricity` `Escaping` `Radius` `PlanetRadius` |
| 环境 | `Gravity` `AirDensity` `DynamicPressure` `AtmosphereTop` |
| 目标 | `TargetDistance` `TargetBearing` `TargetLongitude` `HasTarget` |

`Apoapsis` 这类单位是米 `Speed` 是 m/s `Fuel` 是 0~1 的比例

### 例子

`examples/` 里的程序都能直接粘贴进「代码」视图跑：

| 文件 | 内容 |
| --- | --- |
| `demo1_launch_to_orbit.txt` | 发射到入轨 自己压头、抬拱点、圆化 |
| `demo2_hover_hold.txt` | 悬停 5 秒 再定高 200 m |
| `demo3_rover.txt` | 漫游车直行、转向、停车 |
| `demo4_landing.txt` | 展腿、慢降、开伞、软着陆 |
| `demo5_orbit_cruise.txt` | 时间加速、顺行点火、逆行减速 |
| `demo6_functions_events.txt` | 函数、事件、循环、随机数 |
| `demo7_target_nav.txt` | 转向地图目标并报距离 |
| `demo8_error_demo.txt` | 故意出错 看红色错误条 |
| `mechjeb.txt` | 6 个模式的自动驾驶：发射 / 圆化 / 改拱点 / 降落 / 伞降 / 导航 |
| `landing.txt` | 手写比例控制的降落 适合研究 PID 原理 |

### 几个坑

- `wait`、`if`、`while` 的条件能连写：`while tries < 12 and abs(Apoapsis - Ap) > tol`
- 发射台上还没入轨 `Apoapsis` 会按当前高度算 别指望它有值
- `hold` 和 `burn` 会先等时间加速退出 加速状态下点火是无效的
- 中文版的界面是中文 但代码关键字和变量名还是英文
- 选部件用编号时 先按 **H** 看看各部件是几号
