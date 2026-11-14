// SFS Vizzy 原生编辑器（骨架）：Win32 + D3D11 + ImGui，通过 WebSocket 连模组
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <tchar.h>

#include "imgui/imgui.h"
#include "imgui/backends/imgui_impl_win32.h"
#include "imgui/backends/imgui_impl_dx11.h"

#include "net.h"

#include "json.hpp"
using json = nlohmann::json;

#include <string>
#include <vector>
#include <map>
#include <deque>
#include <algorithm>
#include <cstdio>
#include <cmath>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "ws2_32.lib")

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

// ---------------- 界面状态 ----------------
struct AppState
{
    WsClient ws;
    std::string host = "127.0.0.1";
    int port = 0;                       // 0 = 自动找
    bool running = false;
    bool paused = false;
    bool hasRocket = false;
    bool connected = false;

    std::map<std::string, double> vars;
    std::deque<std::string> log;
    std::string programText;            // 当前程序（原始 JSON，后续换成代码文本）
    std::string programSummary = "-";
    std::string lastError;
};

static AppState g;

static void PushLog(const std::string& line)
{
    g.log.push_back(line);
    while (g.log.size() > 400) g.log.pop_front();
}

// 发给模组的控制消息
static void SendCommand(const char* type)
{
    if (!g.ws.IsConnected()) { PushLog("not connected"); return; }
    std::string msg = std::string("{\"type\":\"") + type + "\"}";
    if (!g.ws.Send(msg)) PushLog("send failed");
}

static void HandleMessage(const std::string& text)
{
    json j;
    try { j = json::parse(text); }
    catch (const std::exception& e) { PushLog(std::string("bad json: ") + e.what()); return; }

    std::string type = j.value("type", "");

    if (type == "state")
    {
        g.running = j.value("running", false);
        g.paused = j.value("paused", false);
        g.hasRocket = j.value("rocket", false);

        g.vars.clear();
        for (auto it = j.begin(); it != j.end(); ++it)
        {
            const std::string& k = it.key();
            if (k == "type" || k == "running" || k == "paused" || k == "rocket" ||
                k == "situation" || k == "planet" || k == "uvars" || k == "blockId" || k == "t")
                continue;
            if (it.value().is_number())
                g.vars[k] = it.value().get<double>();
        }
    }
    else if (type == "log")
    {
        PushLog(j.value("line", ""));
    }
    else if (type == "error")
    {
        std::string t = j.value("message", j.value("line", "error"));
        g.lastError = t;
        PushLog("ERROR: " + t);
    }
    else if (type == "runstate")
    {
        g.running = j.value("running", false);
    }
    else if (type == "program")
    {
        if (j.contains("program") && j["program"].is_object())
        {
            const json& p = j["program"];
            g.programText = p.dump(2);

            int launches = 0, blocks = 0, events = 0, funcs = 0;
            try
            {
                if (p.contains("launches") && p["launches"].is_array())
                {
                    launches = (int)p["launches"].size();
                    for (const auto& l : p["launches"])
                        if (l.contains("blocks") && l["blocks"].is_array()) blocks += (int)l["blocks"].size();
                }
                if (p.contains("events") && p["events"].is_array()) events = (int)p["events"].size();
                if (p.contains("functions") && p["functions"].is_array()) funcs = (int)p["functions"].size();
            }
            catch (const std::exception&) { }

            char buf[160];
            snprintf(buf, sizeof(buf), "%d start, %d blocks, %d events, %d funcs", launches, blocks, events, funcs);
            g.programSummary = buf;
            PushLog(std::string("program: ") + g.programSummary);
        }
    }
}

// ---------------- D3D11 骨架 ----------------
static ID3D11Device* g_pd3dDevice = nullptr;
static ID3D11DeviceContext* g_pd3dDeviceContext = nullptr;
static IDXGISwapChain* g_pSwapChain = nullptr;
static UINT g_ResizeWidth = 0, g_ResizeHeight = 0;
static ID3D11RenderTargetView* g_mainRenderTargetView = nullptr;

static bool CreateDeviceD3D(HWND hWnd)
{
    DXGI_SWAP_CHAIN_DESC sd;
    ZeroMemory(&sd, sizeof(sd));
    sd.BufferCount = 2;
    sd.BufferDesc.Width = 0;
    sd.BufferDesc.Height = 0;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferDesc.RefreshRate.Numerator = 60;
    sd.BufferDesc.RefreshRate.Denominator = 1;
    sd.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hWnd;
    sd.SampleDesc.Count = 1;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    UINT createDeviceFlags = 0;
    D3D_FEATURE_LEVEL featureLevel;
    const D3D_FEATURE_LEVEL featureLevelArray[2] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0 };
    if (D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, createDeviceFlags,
        featureLevelArray, 2, D3D11_SDK_VERSION, &sd, &g_pSwapChain, &g_pd3dDevice,
        &featureLevel, &g_pd3dDeviceContext) != S_OK)
        return false;

    ID3D11Texture2D* pBackBuffer = nullptr;
    g_pSwapChain->GetBuffer(0, IID_PPV_ARGS(&pBackBuffer));
    if (pBackBuffer)
    {
        g_pd3dDevice->CreateRenderTargetView(pBackBuffer, nullptr, &g_mainRenderTargetView);
        pBackBuffer->Release();
    }
    return true;
}

static void CleanupRenderTarget()
{
    if (g_mainRenderTargetView) { g_mainRenderTargetView->Release(); g_mainRenderTargetView = nullptr; }
}

static void CleanupDeviceD3D()
{
    CleanupRenderTarget();
    if (g_pSwapChain) { g_pSwapChain->Release(); g_pSwapChain = nullptr; }
    if (g_pd3dDeviceContext) { g_pd3dDeviceContext->Release(); g_pd3dDeviceContext = nullptr; }
    if (g_pd3dDevice) { g_pd3dDevice->Release(); g_pd3dDevice = nullptr; }
}

static void CreateRenderTarget()
{
    ID3D11Texture2D* pBackBuffer = nullptr;
    g_pSwapChain->GetBuffer(0, IID_PPV_ARGS(&pBackBuffer));
    if (pBackBuffer)
    {
        g_pd3dDevice->CreateRenderTargetView(pBackBuffer, nullptr, &g_mainRenderTargetView);
        pBackBuffer->Release();
    }
}

static LRESULT WINAPI WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    if (ImGui_ImplWin32_WndProcHandler(hWnd, msg, wParam, lParam))
        return true;

    switch (msg)
    {
    case WM_SIZE:
        if (wParam == SIZE_MINIMIZED) return 0;
        g_ResizeWidth = (UINT)LOWORD(lParam);
        g_ResizeHeight = (UINT)HIWORD(lParam);
        return 0;
    case WM_SYSCOMMAND:
        if ((wParam & 0xfff0) == SC_KEYMENU) return 0;
        break;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProc(hWnd, msg, wParam, lParam);
}

// ---------------- 界面 ----------------
static void DrawTopBar()
{
    ImGui::TextUnformatted(g.ws.IsConnected() ? "connected" : "disconnected");
    ImGui::SameLine();
    if (g.ws.IsConnected())
        ImGui::TextDisabled("port %d", g.ws.Port());
    else
        ImGui::TextDisabled("port auto");

    ImGui::SameLine(0, 16);
    if (ImGui::Button("Connect")) { g.ws.Connect(g.host, g.port, "/ws"); g.ws.Send("{\"type\":\"hello\"}"); }
    ImGui::SameLine();
    if (ImGui::Button("Disconnect")) g.ws.Close();

    ImGui::SameLine(0, 24);
    ImGui::BeginDisabled(!g.ws.IsConnected() || g.running);
    if (ImGui::Button("Run")) SendCommand("run");
    ImGui::EndDisabled();

    ImGui::SameLine();
    ImGui::BeginDisabled(!g.running);
    if (ImGui::Button(g.paused ? "Resume" : "Pause")) SendCommand(g.paused ? "resume" : "pause");
    ImGui::SameLine();
    if (ImGui::Button("Step")) SendCommand("step");
    ImGui::SameLine();
    if (ImGui::Button("Stop")) SendCommand("stop");
    ImGui::EndDisabled();

    ImGui::SameLine(0, 24);
    if (ImGui::Button("Reload program")) SendCommand("get");

    ImGui::SameLine(0, 24);
    if (g.running) ImGui::TextColored(ImVec4(0.4f, 0.9f, 0.4f, 1), g.paused ? "PAUSED" : "RUNNING");
    else ImGui::TextDisabled("idle");

    if (!g.hasRocket && g.ws.IsConnected())
    {
        ImGui::SameLine(0, 12);
        ImGui::TextColored(ImVec4(0.95f, 0.7f, 0.3f, 1), "no rocket in flight");
    }
}

static void DrawTelemetry()
{
    ImGui::TextDisabled("Telemetry");
    ImGui::Separator();

    ImGui::BeginChild("telem", ImVec2(0, 0), false);
    if (g.vars.empty())
    {
        ImGui::TextDisabled(g.ws.IsConnected() ? "waiting for state..." : "not connected");
    }
    else
    {
        if (ImGui::BeginTable("telemTable", 2, ImGuiTableFlags_RowBg | ImGuiTableFlags_SizingStretchProp))
        {
            for (const auto& kv : g.vars)
            {
                ImGui::TableNextRow();
                ImGui::TableNextColumn();
                ImGui::TextUnformatted(kv.first.c_str());
                ImGui::TableNextColumn();
                double v = kv.second;
                if (std::fabs(v) >= 1000.0) ImGui::Text("%.0f", v);
                else ImGui::Text("%.3f", v);
            }
            ImGui::EndTable();
        }
    }
    ImGui::EndChild();
}

static void DrawRightPane()
{
    ImGui::TextDisabled("Program: %s", g.programSummary.c_str());
    ImGui::Separator();

    // 程序详情（暂时是原始 JSON，代码视图要等服务端解析器）
    if (ImGui::CollapsingHeader("Program JSON"))
    {
        ImGui::BeginChild("progjson", ImVec2(0, 160), true);
        ImGui::TextUnformatted(g.programText.c_str());
        ImGui::EndChild();
    }

    ImGui::TextDisabled("Console");
    ImGui::Separator();

    ImGui::BeginChild("console", ImVec2(0, 0), false);
    for (const auto& line : g.log)
        ImGui::TextUnformatted(line.c_str());
    if (ImGui::GetScrollY() >= ImGui::GetScrollMaxY() - 1.0f)
        ImGui::SetScrollHereY(1.0f);
    ImGui::EndChild();
}

static void DrawUI()
{
    ImGuiIO& io = ImGui::GetIO();
    ImGui::SetNextWindowPos(ImVec2(0, 0));
    ImGui::SetNextWindowSize(io.DisplaySize);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowRounding, 0.0f);
    ImGui::Begin("##root", nullptr,
        ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove |
        ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoBringToFrontOnFocus);
    ImGui::PopStyleVar();

    DrawTopBar();
    ImGui::Separator();

    ImGui::BeginChild("left", ImVec2(320, 0), true);
    DrawTelemetry();
    ImGui::EndChild();

    ImGui::SameLine();

    ImGui::BeginChild("right", ImVec2(0, 0), true);
    DrawRightPane();
    ImGui::EndChild();

    ImGui::End();
}

// ---------------- 入口 ----------------
static int RunProbe()
{
    if (!g.ws.Connect(g.host, g.port, "/ws"))
    {
        printf("connect failed\n");
        return 1;
    }
    printf("connected on port %d\n", g.ws.Port());
    g.ws.Send("{\"type\":\"hello\"}");

    // 等最多 3 秒，打印第一条消息
    for (int i = 0; i < 60; i++)
    {
        std::string msg;
        if (g.ws.Pop(msg))
        {
            printf("recv: %.400s\n", msg.c_str());
            return 0;
        }
        Sleep(50);
    }
    printf("no message within 3s\n");
    return 2;
}

int main(int argc, char** argv)
{
    bool probe = false;
    for (int i = 1; i < argc; i++)
    {
        std::string a = argv[i];
        if (a == "--probe") probe = true;
        else if (a == "--port" && i + 1 < argc) g.port = atoi(argv[++i]);
        else if (a == "--host" && i + 1 < argc) g.host = argv[++i];
    }

    if (probe) return RunProbe();

    WNDCLASSEXW wc = { sizeof(wc), CS_CLASSDC, WndProc, 0L, 0L, GetModuleHandle(nullptr),
                       nullptr, nullptr, nullptr, nullptr, L"SFSVizzyNative", nullptr };
    RegisterClassExW(&wc);
    HWND hwnd = CreateWindowW(wc.lpszClassName, L"SFS Vizzy (native skeleton)",
        WS_OVERLAPPEDWINDOW, 100, 100, 1280, 800, nullptr, nullptr, wc.hInstance, nullptr);

    if (!CreateDeviceD3D(hwnd))
    {
        CleanupDeviceD3D();
        UnregisterClassW(wc.lpszClassName, wc.hInstance);
        return 1;
    }

    ShowWindow(hwnd, SW_SHOWDEFAULT);
    UpdateWindow(hwnd);

    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;

    ImGui::StyleColorsDark();
    ImGui::GetStyle().WindowRounding = 4.0f;

    // 控制台用等宽字体
    ImFontConfig cfg;
    cfg.OversampleH = 1;
    cfg.OversampleV = 1;
    if (GetFileAttributesA("C:\\Windows\\Fonts\\consola.ttf") != INVALID_FILE_ATTRIBUTES)
        io.Fonts->AddFontFromFileTTF("C:\\Windows\\Fonts\\consola.ttf", 16.0f, &cfg);

    ImGui_ImplWin32_Init(hwnd);
    ImGui_ImplDX11_Init(g_pd3dDevice, g_pd3dDeviceContext);

    // 起来就试着连一次，多半游戏没开，失败就等手动点
    if (g.ws.Connect(g.host, g.port, "/ws"))
    {
        g.ws.Send("{\"type\":\"hello\"}");
        PushLog("connected to mod");
    }
    else
    {
        PushLog("mod not found on 8765-8795 (start the game and press Connect)");
    }

    bool done = false;
    while (!done)
    {
        MSG msg;
        while (PeekMessage(&msg, nullptr, 0U, 0U, PM_REMOVE))
        {
            TranslateMessage(&msg);
            DispatchMessage(&msg);
            if (msg.message == WM_QUIT) done = true;
        }
        if (done) break;

        if (g_ResizeWidth != 0 && g_ResizeHeight != 0)
        {
            CleanupRenderTarget();
            g_pSwapChain->ResizeBuffers(0, g_ResizeWidth, g_ResizeHeight, DXGI_FORMAT_UNKNOWN, 0);
            g_ResizeWidth = g_ResizeHeight = 0;
            CreateRenderTarget();
        }

        // 网络线程收到的消息在这里消费
        std::string inc;
        while (g.ws.Pop(inc)) HandleMessage(inc);
        g.connected = g.ws.IsConnected();

        ImGui_ImplDX11_NewFrame();
        ImGui_ImplWin32_NewFrame();
        ImGui::NewFrame();

        DrawUI();

        ImGui::Render();
        const float clear[4] = { 0.08f, 0.09f, 0.11f, 1.0f };
        g_pd3dDeviceContext->OMSetRenderTargets(1, &g_mainRenderTargetView, nullptr);
        g_pd3dDeviceContext->ClearRenderTargetView(g_mainRenderTargetView, clear);
        ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());

        g_pSwapChain->Present(1, 0);
    }

    g.ws.Close();
    ImGui_ImplDX11_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();

    CleanupDeviceD3D();
    DestroyWindow(hwnd);
    UnregisterClassW(wc.lpszClassName, wc.hInstance);
    return 0;
}
