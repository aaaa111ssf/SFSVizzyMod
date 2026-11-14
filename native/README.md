# SFS Vizzy 原生编辑器（C++ / ImGui）— 骨架版

把浏览器里的编辑器换成一个原生窗口，直接连模组的 WebSocket。
**分支 `imgui-editor`，未合入 main。**

## 现在能做什么

| 功能 | 状态 |
| --- | --- |
| 自动找到模组（扫描 8765–8795）并连上 | 可用 |
| 遥测面板：模组推来的所有变量，10 Hz 刷新 | 可用 |
| 控制台：模组日志、错误、程序摘要 | 可用 |
| Run / Stop / Pause / Resume / Step | 可用 |
| 读取当前程序（`get`）并显示块数统计 + 原始 JSON | 可用 |
| 代码视图（可编辑，Ctrl+Enter 应用） | **未做**，见下 |

## 为什么代码视图还没做

模组只接受积木的 JSON AST，不接受代码文本。要把代码变成积木，必须有一个解析器：

- 网页版里是 `parseCode` / `programToCode`，247 + 131 行 JavaScript，46 条正则；
- C++ 端重写一份等于两份逻辑长期并行维护，改一处必须改两处。

所以下一步是在**模组里用 C# 实现**解析和生成，然后给客户端加一条 `setCode` 消息。
这样网页版和原生版共用同一份解析器，也能顺便给 C# 补单元测试。

## 构建

需要 MinGW-w64（GCC 16 实测通过）。默认找 `D:\tools\mingw64`，可用 `MINGW` 环境变量覆盖：

```
set MINGW=D:\tools\mingw64
build.bat
```

Git Bash：

```
MINGW=/d/tools/mingw64 bash build.sh
```

产物在 `native/build/`，已经带上 `libstdc++-6.dll` 等三个运行库，整个目录拷走就能双击运行。

## 运行

```
build\sfsvizzy-native.exe                 自动找端口并连接
build\sfsvizzy-native.exe --port 8899     指定端口（测试用）
build\sfsvizzy-native.exe --probe         不开窗口，连上就打印一条消息后退出
```

`--probe` 是给自动化验证用的：不用弹窗口也能确认握手和帧解析是否正常。

## 依赖

都是随仓库带着的源码，没有二进制库：

| 目录 | 用途 |
| --- | --- |
| `third_party/imgui` | ImGui 1.91.8，含 Win32 + DX11 后端 |
| `third_party/json.hpp` | nlohmann/json 3.11.3，解析遥测 |
| `src/net.cpp` | 自己写的 WebSocket 客户端（Winsock，约 250 行） |

没用 GLFW：ImGui 自带的 Win32 + D3D11 后端不需要额外东西，MinGW 也自带链接库。
`cpp-httplib` 下载下来发现这个版本没有 WebSocket 客户端，所以干脆自己写。

## 协议（模组侧，`src/Bridge.cs`）

客户端发：

```json
{"type":"hello"}                     → 回 program + runstate
{"type":"run","program":{...}}       → 运行；不 program 就重跑上一份
{"type":"stop"} {"type":"pause"} {"type":"resume"} {"type":"step"}
{"type":"get"}                       → 再推一次 program
```

服务端推：`state`（10 Hz，全部变量平铺）、`log`、`error`、`runstate`、`program`。

## 已知问题

- 控制台窗口跟着 exe 一起出现，还没改成隐藏（方便看崩溃信息）。
- 没有图标、没有设置持久化、窗口大小不记忆。
- 断开连接不会自动重连，需要点 Connect。
