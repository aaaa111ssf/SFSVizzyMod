// 手写的 WebSocket 客户端：握手 + 帧读写，够用就行
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>

#include "net.h"

#include <cstdio>
#include <cstring>
#include <cstdlib>

#ifdef _MSC_VER
#pragma comment(lib, "ws2_32.lib")
#endif

namespace
{
    std::string Base64(const unsigned char* data, int len)
    {
        static const char* tbl = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        std::string out;
        int i = 0;
        while (i + 2 < len)
        {
            int v = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
            out += tbl[(v >> 18) & 63];
            out += tbl[(v >> 12) & 63];
            out += tbl[(v >> 6) & 63];
            out += tbl[v & 63];
            i += 3;
        }
        if (i < len)
        {
            int v = data[i] << 16;
            out += tbl[(v >> 18) & 63];
            out += tbl[(v >> 12) & 63];
            out += (i + 1 < len) ? tbl[(v >> 6) & 63] : '=';
            out += '=';
        }
        return out;
    }

    // 收满 n 字节；断了返回 false
    bool RecvAll(SOCKET s, char* buf, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r = recv(s, buf + got, n - got, 0);
            if (r <= 0) return false;
            got += r;
        }
        return true;
    }

    // 发一帧文本（客户端必须加掩码）
    bool SendFrame(SOCKET s, const std::string& text)
    {
        unsigned char head[14];
        int hlen = 0;
        head[hlen++] = 0x81;   // FIN + 文本帧
        size_t n = text.size();
        if (n < 126)
        {
            head[hlen++] = (unsigned char)(0x80 | n);
        }
        else if (n <= 0xFFFF)
        {
            head[hlen++] = 0x80 | 126;
            head[hlen++] = (unsigned char)(n >> 8);
            head[hlen++] = (unsigned char)(n & 0xFF);
        }
        else
        {
            head[hlen++] = 0x80 | 127;
            for (int i = 7; i >= 0; i--) head[hlen++] = (unsigned char)((unsigned long long)n >> (i * 8));
        }

        unsigned char mask[4] = { 0x37, 0x11, 0x53, 0x99 };
        memcpy(head + hlen, mask, 4);
        hlen += 4;

        std::string frame((const char*)head, hlen);
        frame.resize(hlen + n);
        for (size_t i = 0; i < n; i++)
            frame[hlen + i] = (char)((unsigned char)text[i] ^ mask[i % 4]);

        int sent = 0;
        while (sent < (int)frame.size())
        {
            int r = send(s, frame.data() + sent, (int)frame.size() - sent, 0);
            if (r <= 0) return false;
            sent += r;
        }
        return true;
    }
}

bool WsClient::Connect(const std::string& host, int port, const std::string& path)
{
    Close();

    static bool wsaReady = false;
    if (!wsaReady)
    {
        WSADATA wsa;
        if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return false;
        wsaReady = true;
    }

    // 端口 0：在模组的 8765..8795 里挨个试
    int first = port > 0 ? port : 8765;
    int last = port > 0 ? port : 8795;

    for (int p = first; p <= last; p++)
    {
        SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (s == INVALID_SOCKET) continue;

        sockaddr_in addr{};
        addr.sin_family = AF_INET;
        addr.sin_port = htons((unsigned short)p);
        inet_pton(AF_INET, host.c_str(), &addr.sin_addr);

        if (connect(s, (sockaddr*)&addr, sizeof(addr)) != 0)
        {
            closesocket(s);
            continue;
        }

        // 握手：随机 key 用 rand 就够，服务端不回校验
        unsigned char key[16];
        for (int i = 0; i < 16; i++) key[i] = (unsigned char)(rand() & 0xFF);

        char req[512];
        snprintf(req, sizeof(req),
            "GET %s HTTP/1.1\r\n"
            "Host: %s:%d\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            "Sec-WebSocket-Key: %s\r\n"
            "Sec-WebSocket-Version: 13\r\n\r\n",
            path.c_str(), host.c_str(), p, Base64(key, 16).c_str());

        if (send(s, req, (int)strlen(req), 0) <= 0)
        {
            closesocket(s);
            continue;
        }

        // 只读到第一个空行为止，看到 101 就算成功
        std::string resp;
        char c = 0;
        while (resp.size() < 2048)
        {
            if (recv(s, &c, 1, 0) <= 0) break;
            resp += c;
            if (resp.size() >= 4 && resp.compare(resp.size() - 4, 4, "\r\n\r\n") == 0) break;
        }
        if (resp.find("101") == std::string::npos)
        {
            closesocket(s);
            continue;
        }

        sock = (unsigned long long)s;
        usedPort = p;
        stopping = false;
        connected = true;
        reader = std::thread([this]() { ReaderLoop(); });
        return true;
    }
    return false;
}

void WsClient::Close()
{
    stopping = true;
    connected = false;
    if (sock != (unsigned long long)-1)
    {
        shutdown((SOCKET)sock, SD_BOTH);
        closesocket((SOCKET)sock);
        sock = (unsigned long long)-1;
    }
    if (reader.joinable()) reader.join();

    std::lock_guard<std::mutex> lock(qm);
    inbox.clear();
}

bool WsClient::Send(const std::string& text)
{
    if (!connected || sock == (unsigned long long)-1) return false;
    return SendFrame((SOCKET)sock, text);
}

bool WsClient::Pop(std::string& out)
{
    std::lock_guard<std::mutex> lock(qm);
    if (inbox.empty()) return false;
    out = inbox.front();
    inbox.pop_front();
    return true;
}

void WsClient::ReaderLoop()
{
    SOCKET s = (SOCKET)sock;
    char head[2];

    while (connected && !stopping)
    {
        if (!RecvAll(s, head, 2)) break;

        int opcode = head[0] & 0x0F;
        bool masked = (head[1] & 0x80) != 0;
        unsigned long long len = head[1] & 0x7F;
        unsigned char ext[8];

        if (len == 126)
        {
            if (!RecvAll(s, (char*)ext, 2)) break;
            len = ((unsigned long long)ext[0] << 8) | ext[1];
        }
        else if (len == 127)
        {
            if (!RecvAll(s, (char*)ext, 8)) break;
            len = 0;
            for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
        }
        if (len > 4 * 1024 * 1024) break;   // 明显不对，断开

        unsigned char mask[4] = { 0, 0, 0, 0 };
        if (masked && !RecvAll(s, (char*)mask, 4)) break;

        std::string payload;
        payload.resize((size_t)len);
        if (len > 0 && !RecvAll(s, &payload[0], (int)len)) break;
        if (masked)
            for (size_t i = 0; i < payload.size(); i++) payload[i] ^= mask[i % 4];

        if (opcode == 0x8) break;            // 关闭
        if (opcode == 0x9)                   // ping → pong（空帧，够用）
        {
            unsigned char pong[2] = { 0x8A, 0x80 };
            send(s, (const char*)pong, 2, 0);
            continue;
        }
        if (opcode == 0x1 || opcode == 0x2)
        {
            std::lock_guard<std::mutex> lock(qm);
            inbox.push_back(payload);
            if (inbox.size() > 200) inbox.pop_front();
        }
    }

    connected = false;
}
