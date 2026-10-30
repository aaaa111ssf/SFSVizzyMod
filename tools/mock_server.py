"""本地假游戏服务器：仅用于前端验收（HTTP + WebSocket，协议与模组一致）。"""
import base64
import hashlib
import json
import socket
import struct
import threading
import time
import os

GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HTML = open(os.path.join(ROOT, "web", "index.html"), encoding="utf-8").read()

PROGRAM = {
    "launches": [
        {"blocks": [
            {"kind": "wait", "a": "1", "op": "<=", "b": ""},
            {"kind": "set_throttle", "a": "1", "op": "<=", "b": ""},
            {"kind": "log", "a": "Liftoff!", "op": "<=", "b": ""},
            {"kind": "dialog", "title": "Notice", "a": "Hello {planet}", "dir": "message", "var": "answer", "op": "<=", "b": "0"},
            {"kind": "engine", "a": "all", "dir": "off", "op": "<=", "b": ""},
            {"kind": "parachute", "a": "1", "dir": "deploy", "op": "<=", "b": ""},
            {"kind": "stage", "a": "2", "op": "<=", "b": ""},
            {"kind": "while", "a": "AltitudeAGL", "op": "<=", "b": "10000", "body": [
                {"kind": "if", "a": "TWR", "op": ">=", "b": "1.2",
                 "then": [{"kind": "set_throttle", "a": "Throttle - 0.05", "op": "<=", "b": ""},
                          {"kind": "log", "a": "Reduced thrust", "op": "<=", "b": ""}],
                 "els": [{"kind": "set_throttle", "a": "Throttle + 0.05", "op": "<=", "b": ""},
                         {"kind": "log", "a": "Increased thrust", "op": "<=", "b": ""}]},
                {"kind": "wait", "a": "0.2", "op": "<=", "b": ""}
            ]},
            {"kind": "log", "a": "Ascent complete", "op": "<=", "b": ""}
        ]},
        {"blocks": [
            {"kind": "wait_until", "a": "AltitudeAGL", "op": ">", "b": "2000", "dir": None},
            {"kind": "log", "a": "Second ON START: staging at {AltitudeAGL} m", "op": "<=", "b": ""},
            {"kind": "stage", "a": "", "op": "<=", "b": ""}
        ]}
    ],
    "triggers": [
        {"mode": "agl", "op": ">=", "value": "5000", "once": True, "blocks": [
            {"kind": "log", "a": "Trigger: 5 km true altitude ({AltitudeAGL} m)", "op": "<=", "b": ""},
            {"kind": "turn", "dir": "right", "a": "10", "op": "<=", "b": "1"}
        ]}
    ],
    "events": [
        {"type": "key", "key": "R", "once": True, "blocks": [
            {"kind": "log", "a": "Key R event", "op": "<=", "b": ""}
        ]},
        {"type": "landed", "once": True, "blocks": [
            {"kind": "log", "a": "Landed event ({VerticalSpeed} m/s)", "op": "<=", "b": ""}
        ]}
    ],
    "variables": {}
}

clients = []
lock = threading.RLock()


def recv_exact(conn, n):
    buf = b""
    while len(buf) < n:
        chunk = conn.recv(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return buf


def ws_send(conn, text):
    data = text.encode("utf-8")
    header = bytearray([0x81])
    n = len(data)
    if n < 126:
        header.append(n)
    elif n <= 65535:
        header.append(126)
        header += struct.pack(">H", n)
    else:
        header.append(127)
        header += struct.pack(">Q", n)
    with lock:
        conn.sendall(bytes(header) + data)


def ws_recv(conn):
    hdr = recv_exact(conn, 2)
    if not hdr:
        return None
    b0, b1 = hdr[0], hdr[1]
    op = b0 & 0x0F
    masked = b1 & 0x80
    ln = b1 & 0x7F
    if ln == 126:
        ln = struct.unpack(">H", recv_exact(conn, 2))[0]
    elif ln == 127:
        ln = struct.unpack(">Q", recv_exact(conn, 8))[0]
    mask = recv_exact(conn, 4) if masked else None
    data = recv_exact(conn, ln)
    if data is None:
        return None
    if mask:
        data = bytes(c ^ mask[i % 4] for i, c in enumerate(data))
    if op == 0x8:
        return None
    if op == 0x9:
        return ""
    return data.decode("utf-8", "replace")


def state_loop():
    t0 = time.time()
    while True:
        time.sleep(0.2)
        alt = 120 + (time.time() - t0) * 45
        payload = {
            "type": "state", "running": False, "rocket": True,
            "planet": "Earth", "situation": "flight",
            "Throttle": 0.82, "Altitude": alt + 60, "AltitudeAGL": alt,
            "Speed": 180 + alt * 0.05, "VerticalSpeed": 42.5,
            "HorizontalSpeed": 12.3, "Mass": 18.4, "TWR": 1.26, "MaxTWR": 1.54,
            "Fuel": 0.67, "FuelAmount": 9.8, "FuelCapacity": 14.6,
            "Apoapsis": 125000, "Periapsis": -24000, "Eccentricity": 0.42,
            "Escaping": 0, "Heading": 12.5, "AngularVel": -0.8,
            "Gravity": 9.81, "AirDensity": 1.225, "DynamicPressure": 8420,
            "AtmosphereTop": 70000
        }
        with lock:
            dead = []
            for c in clients:
                try:
                    ws_send(c, json.dumps(payload))
                except Exception:
                    dead.append(c)
            for c in dead:
                clients.remove(c)


def handle(conn):
    try:
        raw = b""
        while b"\r\n\r\n" not in raw:
            chunk = conn.recv(4096)
            if not chunk:
                return
            raw += chunk
        head = raw.split(b"\r\n\r\n")[0].decode("latin-1")
        lines = head.split("\r\n")
        method, path = lines[0].split(" ")[:2]
        print("request:", method, path, flush=True)
        headers = {}
        for line in lines[1:]:
            if ":" in line:
                k, v = line.split(":", 1)
                headers[k.strip().lower()] = v.strip()

        if path.startswith("/ws") and "websocket" in headers.get("upgrade", "").lower():
            key = headers.get("sec-websocket-key", "")
            accept = base64.b64encode(hashlib.sha1((key + GUID).encode()).digest()).decode()
            conn.sendall((
                "HTTP/1.1 101 Switching Protocols\r\n"
                "Upgrade: websocket\r\nConnection: Upgrade\r\n"
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n"
            ).encode())
            with lock:
                clients.append(conn)
            while True:
                msg = ws_recv(conn)
                if msg is None:
                    break
                if not msg:
                    continue
                try:
                    obj = json.loads(msg)
                except Exception:
                    continue
                print("browser msg:", msg[:80], flush=True)
                if obj.get("type") == "hello":
                    ws_send(conn, json.dumps({"type": "program", "program": PROGRAM}))
                    ws_send(conn, json.dumps({"type": "runstate", "running": False}))
                    ws_send(conn, json.dumps({"type": "log", "line": "mock game connected"}))
                    # 模拟游戏里按 R：要求浏览器把当前脚本发过来
                    threading.Timer(1.2, lambda: ws_send(conn, json.dumps({"type": "request_run"}))).start()
                elif obj.get("type") == "get":
                    ws_send(conn, json.dumps({"type": "program", "program": PROGRAM}))
                elif obj.get("type") == "save":
                    ws_send(conn, json.dumps({"type": "log", "line": "Program saved (mock)"}))
                elif obj.get("type") == "run":
                    ws_send(conn, json.dumps({"type": "runstate", "running": True}))
                    ws_send(conn, json.dumps({"type": "log", "line": "program running (mock)"}))
                elif obj.get("type") == "stop":
                    ws_send(conn, json.dumps({"type": "runstate", "running": False}))
            with lock:
                if conn in clients:
                    clients.remove(conn)
            return

        if path in ("/", "/index.html"):
            body = HTML.encode("utf-8")
            conn.sendall((
                "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n"
                "Content-Length: %d\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n" % len(body)
            ).encode() + body)
        else:
            conn.sendall(b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
    except Exception as e:
        print("handler error:", e, flush=True)
    finally:
        try:
            conn.close()
        except Exception:
            pass


def handle_log(conn):
    print("connection accepted", flush=True)
    handle(conn)


def main():
    threading.Thread(target=state_loop, daemon=True).start()
    srv = socket.socket()
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", 8899))
    srv.listen(8)
    print("mock server on http://127.0.0.1:8899/")
    while True:
        c, _ = srv.accept()
        threading.Thread(target=handle, args=(c,), daemon=True).start()


if __name__ == "__main__":
    main()
