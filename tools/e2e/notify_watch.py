#!/usr/bin/env python3
"""Logs PINS user notifications (SignalR hub /hubs/notifications on port 4782).

Notification.ShowError/ShowWarning/... in pins are not written to the PINS log;
they are broadcast to UIs through this hub. The e2e harness records them so a
failure message such as "connect the mount before starting guiding" can be
asserted.

Output: one line per notification: <time> NOTIFY <json arguments>

Usage: notify_watch.py HOST PORT [SECONDS]
"""
import json
import sys
import threading
import time

import websocket  # websocket-client

host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
port = int(sys.argv[2]) if len(sys.argv) > 2 else 4782
duration = float(sys.argv[3]) if len(sys.argv) > 3 else 1e9
RS = "\x1e"


def out(line):
    print(f"{time.strftime('%H:%M:%S')} {line}", flush=True)


def session(deadline):
    ws = websocket.create_connection(f"ws://{host}:{port}/hubs/notifications", timeout=5)
    ws.send(json.dumps({"protocol": "json", "version": 1}) + RS)
    out("CONNECTED")
    stop = threading.Event()

    def pinger():
        while not stop.wait(10):
            try:
                ws.send(json.dumps({"type": 6}) + RS)
            except Exception:
                return

    threading.Thread(target=pinger, daemon=True).start()
    buf = ""
    try:
        while time.time() < deadline:
            try:
                buf += ws.recv()
            except websocket.WebSocketTimeoutException:
                continue
            while RS in buf:
                frame, buf = buf.split(RS, 1)
                if not frame.strip():
                    continue
                msg = json.loads(frame)
                if msg.get("type") == 1:
                    out(f"NOTIFY {msg.get('target')} {json.dumps(msg.get('arguments'), ensure_ascii=False)}")
                elif msg.get("type") == 7:
                    out(f"CLOSE {json.dumps(msg)}")
                    return
    finally:
        stop.set()
        ws.close()


def main():
    deadline = time.time() + duration
    while time.time() < deadline:
        try:
            session(deadline)
        except Exception as ex:  # reconnect while pins restarts
            out(f"ERROR {ex!r}")
            time.sleep(2)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
