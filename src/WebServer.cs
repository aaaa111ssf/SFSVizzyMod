using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace SFSVizzyMod
{
    // 极简本地 Web 服务：只绑 127.0.0.1，提供页面、程序接口和 WebSocket。
    public class WebServer
    {
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;
        private readonly List<Client> clients = new List<Client>();
        private readonly object clientsLock = new object();

        // 浏览器 → 主线程的指令队列（JSON 文本）。
        public readonly ConcurrentQueue<string> Inbox = new ConcurrentQueue<string>();

        public int Port { get; private set; } = -1;
        public string Html = "<h1>SFS Vizzy: web/index.html missing</h1>";

        public bool Running => running;

        public int Start(int fromPort, int toPort)
        {
            for (int p = fromPort; p <= toPort; p++)
            {
                try
                {
                    TcpListener l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                    listener = l;
                    Port = p;
                    running = true;
                    acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "SFSVizzyMod.Web" };
                    acceptThread.Start();
                    Debug.Log("[SFSVizzy] Web UI listening on http://127.0.0.1:" + p + "/");
                    return p;
                }
                catch (Exception)
                {
                    // 端口占用，换下一个
                }
            }
            Debug.LogError("[SFSVizzy] Could not bind any port in " + fromPort + ".." + toPort);
            return -1;
        }

        public void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch (Exception) { }
            listener = null;
            Client[] snapshot;
            lock (clientsLock)
            {
                snapshot = clients.ToArray();
                clients.Clear();
            }
            for (int i = 0; i < snapshot.Length; i++) snapshot[i].Close();
        }

        public void Broadcast(string json)
        {
            Client[] snapshot;
            lock (clientsLock)
            {
                if (clients.Count == 0) return;
                snapshot = clients.ToArray();
            }
            for (int i = 0; i < snapshot.Length; i++) snapshot[i].Send(json);
        }

        public bool HasClients
        {
            get { lock (clientsLock) { return clients.Count > 0; } }
        }

        // ---------------- 接受连接 ----------------

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient tcp;
                try { tcp = listener.AcceptTcpClient(); }
                catch (Exception) { break; }

                TcpClient captured = tcp;
                Thread t = new Thread(() => Handle(captured)) { IsBackground = true };
                t.Start();
            }
        }

        private void Handle(TcpClient tcp)
        {
            Client client = null;
            try
            {
                tcp.NoDelay = true;
                NetworkStream stream = tcp.GetStream();
                tcp.ReceiveTimeout = 10000;

                Request req = ReadRequest(stream);
                if (req == null) return;

                string upgrade;
                req.Headers.TryGetValue("upgrade", out upgrade);
                if (req.Path == "/ws" && upgrade != null && upgrade.ToLowerInvariant().Contains("websocket"))
                {
                    string key;
                    if (!req.Headers.TryGetValue("sec-websocket-key", out key) || string.IsNullOrEmpty(key))
                    {
                        WriteResponse(stream, 400, "text/plain", "missing key");
                        return;
                    }

                    string accept = Convert.ToBase64String(SHA1.Create().ComputeHash(
                        Encoding.ASCII.GetBytes(key + WebSocketGuid)));

                    string handshake =
                        "HTTP/1.1 101 Switching Protocols\r\n" +
                        "Upgrade: websocket\r\n" +
                        "Connection: Upgrade\r\n" +
                        "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
                    byte[] hb = Encoding.ASCII.GetBytes(handshake);
                    stream.Write(hb, 0, hb.Length);

                    tcp.ReceiveTimeout = 0;   // 长连接
                    client = new Client(tcp, stream);
                    lock (clientsLock) clients.Add(client);

                    WsLoop(client);
                    return;
                }

                if (req.Path == "/" || req.Path == "/index.html")
                {
                    WriteResponse(stream, 200, "text/html; charset=utf-8", Html);
                }
                else if (req.Path == "/api/program")
                {
                    if (req.Method == "GET")
                    {
                        WriteResponse(stream, 200, "application/json; charset=utf-8", ProgramIO.LoadRaw());
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(req.Body)) Inbox.Enqueue(req.Body);
                        WriteResponse(stream, 200, "application/json", "{\"ok\":true}");
                    }
                }
                else if (req.Path == "/favicon.ico")
                {
                    WriteResponse(stream, 204, "text/plain", "");
                }
                else
                {
                    WriteResponse(stream, 404, "text/plain", "not found");
                }
            }
            catch (Exception e)
            {
                if (running) Debug.LogWarning("[SFSVizzy] http error: " + e.Message);
            }
            finally
            {
                if (client != null)
                {
                    client.Close();
                    lock (clientsLock) clients.Remove(client);
                }
                else
                {
                    try { tcp.Close(); } catch (Exception) { }
                }
            }
        }

        // ---------------- WebSocket 处理 ----------------

        private void WsLoop(Client client)
        {
            try
            {
                while (running && client.Open)
                {
                    string text;
                    if (!ReadMessage(client, out text)) break;
                    if (!string.IsNullOrEmpty(text)) Inbox.Enqueue(text);
                }
            }
            catch (Exception)
            {
                // 客户端断开
            }
            finally
            {
                client.Close();
                lock (clientsLock) clients.Remove(client);
            }
        }

        // 读一条完整文本消息（处理分片、ping、pong、close）。
        private bool ReadMessage(Client client, out string text)
        {
            text = null;
            MemoryStream acc = new MemoryStream();
            NetworkStream s = client.Stream;

            while (running && client.Open)
            {
                int b0 = s.ReadByte();
                if (b0 < 0) return false;
                int b1 = s.ReadByte();
                if (b1 < 0) return false;

                bool fin = (b0 & 0x80) != 0;
                int opcode = b0 & 0x0F;
                bool masked = (b1 & 0x80) != 0;
                long len = b1 & 0x7F;

                if (len == 126)
                {
                    byte[] ext = ReadExact(s, 2);
                    if (ext == null) return false;
                    len = (ext[0] << 8) | ext[1];
                }
                else if (len == 127)
                {
                    byte[] ext = ReadExact(s, 8);
                    if (ext == null) return false;
                    len = 0;
                    for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
                }

                if (len < 0 || len > 4L * 1024 * 1024) return false;

                byte[] mask = null;
                if (masked)
                {
                    mask = ReadExact(s, 4);
                    if (mask == null) return false;
                }

                byte[] payload = ReadExact(s, (int)len);
                if (payload == null) return false;
                if (masked)
                    for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

                if (opcode == 0x8) return false;                 // close
                if (opcode == 0x9) { client.SendRaw(0xA, payload); continue; } // ping → pong
                if (opcode == 0xA) continue;                     // pong

                if (opcode == 0x1 || opcode == 0x0) acc.Write(payload, 0, payload.Length);

                if (fin)
                {
                    text = Encoding.UTF8.GetString(acc.ToArray());
                    return true;
                }
            }
            return false;
        }

        private static byte[] ReadExact(NetworkStream s, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = s.Read(buffer, offset, count - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return buffer;
        }

        // ---------------- HTTP 处理 ----------------

        private sealed class Request
        {
            public string Method;
            public string Path;
            public string Body;
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>();
        }

        private static Request ReadRequest(NetworkStream s)
        {
            MemoryStream buffer = new MemoryStream();
            int state = 0;   // \r\n\r\n 匹配进度
            while (state < 4)
            {
                int b = s.ReadByte();
                if (b < 0) return null;
                buffer.WriteByte((byte)b);

                char c = (char)b;
                if ((state == 0 || state == 2) && c == '\r') state++;
                else if ((state == 1 || state == 3) && c == '\n') state++;
                else state = c == '\r' ? 1 : 0;

                if (buffer.Length > 64 * 1024) return null;
            }

            string head = Encoding.UTF8.GetString(buffer.ToArray());
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;

            string[] first = lines[0].Split(' ');
            Request req = new Request
            {
                Method = first.Length > 0 ? first[0] : "GET",
                Path = first.Length > 1 ? first[1] : "/"
            };

            int q = req.Path.IndexOf('?');
            if (q >= 0) req.Path = req.Path.Substring(0, q);

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string name = lines[i].Substring(0, colon).Trim().ToLowerInvariant();
                string value = lines[i].Substring(colon + 1).Trim();
                req.Headers[name] = value;
            }

            int contentLength = 0;
            string cl;
            if (req.Headers.TryGetValue("content-length", out cl)) int.TryParse(cl, out contentLength);
            if (contentLength > 0 && contentLength < 4 * 1024 * 1024)
            {
                byte[] body = ReadExact(s, contentLength);
                if (body == null) return null;
                req.Body = Encoding.UTF8.GetString(body);
            }
            return req;
        }

        private static void WriteResponse(NetworkStream s, int code, string contentType, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");
            string reason = code == 200 ? "OK" : code == 204 ? "No Content" : code == 400 ? "Bad Request" : "Not Found";
            string head =
                "HTTP/1.1 " + code + " " + reason + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + bytes.Length + "\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            s.Write(hb, 0, hb.Length);
            if (bytes.Length > 0) s.Write(bytes, 0, bytes.Length);
        }

        // ---------------- 单个连接 ----------------

        private sealed class Client
        {
            public readonly TcpClient Tcp;
            public readonly NetworkStream Stream;
            private readonly object writeLock = new object();
            public volatile bool Open = true;

            public Client(TcpClient tcp, NetworkStream stream)
            {
                Tcp = tcp;
                Stream = stream;
            }

            public void Send(string text)
            {
                SendRaw(0x1, Encoding.UTF8.GetBytes(text));
            }

            public void SendRaw(byte opcode, byte[] payload)
            {
                byte[] frame = BuildFrame(opcode, payload);
                lock (writeLock)
                {
                    if (!Open) return;
                    try { Stream.Write(frame, 0, frame.Length); }
                    catch (Exception) { Open = false; }
                }
            }

            public void Close()
            {
                Open = false;
                try { Tcp.Close(); } catch (Exception) { }
            }

            private static byte[] BuildFrame(byte opcode, byte[] payload)
            {
                int len = payload.Length;
                byte[] header;

                if (len < 126)
                {
                    header = new byte[2];
                    header[1] = (byte)len;
                }
                else if (len <= 65535)
                {
                    header = new byte[4];
                    header[1] = 126;
                    header[2] = (byte)(len >> 8);
                    header[3] = (byte)len;
                }
                else
                {
                    header = new byte[10];
                    header[1] = 127;
                    for (int i = 0; i < 8; i++) header[9 - i] = (byte)(len >> (8 * i));
                }

                header[0] = (byte)(0x80 | opcode);

                byte[] frame = new byte[header.Length + len];
                Buffer.BlockCopy(header, 0, frame, 0, header.Length);
                Buffer.BlockCopy(payload, 0, frame, header.Length, len);
                return frame;
            }
        }
    }
}
