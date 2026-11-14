// 最小 WebSocket 客户端：只做我们需要的文本帧，不引第三方库
#pragma once

#include <string>
#include <deque>
#include <mutex>
#include <thread>
#include <atomic>

class WsClient
{
public:
    ~WsClient() { Close(); }

    // 端口给 0 时自动在 8765..8795 里找（模组会挑空闲端口）
    bool Connect(const std::string& host, int port, const std::string& path);
    void Close();

    bool Send(const std::string& text);
    bool Pop(std::string& out);   // 取一条收到的消息，没有就返回 false
    bool IsConnected() const { return connected; }
    int Port() const { return usedPort; }

private:
    void ReaderLoop();

    unsigned long long sock = (unsigned long long)-1;
    std::thread reader;
    std::atomic<bool> connected{false};
    std::atomic<bool> stopping{false};
    std::mutex qm;
    std::deque<std::string> inbox;
    int usedPort = 0;
};
