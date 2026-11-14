#!/usr/bin/env bash
# 构建原生编辑器。MinGW 路径可以用 MINGW 环境变量覆盖
set -e
cd "$(dirname "$0")"

MINGW="${MINGW:-/d/tools/mingw64}"
export PATH="$MINGW/bin:$PATH"

mkdir -p build

g++ -std=c++17 -O1 -fexceptions \
  -o build/sfsvizzy-native.exe \
  src/main.cpp src/net.cpp \
  third_party/imgui/imgui.cpp \
  third_party/imgui/imgui_draw.cpp \
  third_party/imgui/imgui_tables.cpp \
  third_party/imgui/imgui_widgets.cpp \
  third_party/imgui/backends/imgui_impl_win32.cpp \
  third_party/imgui/backends/imgui_impl_dx11.cpp \
  -Ithird_party -Ithird_party/imgui -Ithird_party/imgui/backends \
  -lgdi32 -ld3d11 -ldxgi -ldwmapi -lws2_32 -ld3dcompiler

# 带上运行库，拷走整个 build 目录就能跑，不用装 MinGW
for f in libstdc++-6.dll libgcc_s_seh-1.dll libwinpthread-1.dll; do
  cp -f "$MINGW/bin/$f" build/ 2>/dev/null || true
done

echo "ok -> native/build/sfsvizzy-native.exe"
