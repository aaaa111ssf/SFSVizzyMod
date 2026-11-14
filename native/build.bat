@echo off
rem 构建原生编辑器。MinGW 路径可以用 MINGW 环境变量覆盖
setlocal
cd /d "%~dp0"

if "%MINGW%"=="" set MINGW=D:\tools\mingw64
set PATH=%MINGW%\bin;%PATH%

if not exist build mkdir build

g++ -std=c++17 -O1 -fexceptions ^
  -o build\sfsvizzy-native.exe ^
  src\main.cpp src\net.cpp ^
  third_party\imgui\imgui.cpp ^
  third_party\imgui\imgui_draw.cpp ^
  third_party\imgui\imgui_tables.cpp ^
  third_party\imgui\imgui_widgets.cpp ^
  third_party\imgui\backends\imgui_impl_win32.cpp ^
  third_party\imgui\backends\imgui_impl_dx11.cpp ^
  -Ithird_party -Ithird_party\imgui -Ithird_party\imgui\backends ^
  -lgdi32 -ld3d11 -ldxgi -ldwmapi -lws2_32 -ld3dcompiler

if errorlevel 1 goto fail

for %%f in (libstdc++-6.dll libgcc_s_seh-1.dll libwinpthread-1.dll) do (
  if exist "%MINGW%\bin\%%f" copy /y "%MINGW%\bin\%%f" build\ >nul
)

echo ok -^> native\build\sfsvizzy-native.exe
exit /b 0

:fail
echo build failed
exit /b 1
