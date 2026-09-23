#!/usr/bin/env python3
"""Minimal INDI client that logs guide pulses and mount coordinates.

Connects to an indiserver, asks for all properties (BLOBs stay disabled) and
prints one line per interesting update:

  PULSE <device> <vector> <state> <element>=<ms> ...   (TELESCOPE_TIMED_GUIDE_NS/WE, any device)
  COORD <device> <state> RA=<h> DEC=<deg>              (EQUATORIAL_EOD_COORD, throttled)
  EXPOSURE <device> <state> <seconds>                  (CCD_EXPOSURE)

Used by the e2e harness to prove that guide pulses reach the INDI telescope
simulator and that the guide camera is exposing.

Usage: indi_watch.py HOST PORT [SECONDS]
"""
import socket
import sys
import time
import xml.etree.ElementTree as ET

host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
port = int(sys.argv[2]) if len(sys.argv) > 2 else 7624
duration = float(sys.argv[3]) if len(sys.argv) > 3 else 1e9

PULSE_VECTORS = {"TELESCOPE_TIMED_GUIDE_NS", "TELESCOPE_TIMED_GUIDE_WE"}


def out(line):
    print(f"{time.strftime('%H:%M:%S')}.{int((time.time() % 1) * 1000):03d} {line}", flush=True)


def main():
    deadline = time.time() + duration
    while time.time() < deadline:
        try:
            run(deadline)
        except (OSError, ET.ParseError) as ex:
            out(f"ERROR {ex!r}; reconnecting")
            time.sleep(2)


def run(deadline):
    sock = socket.create_connection((host, port), timeout=5)
    sock.settimeout(1.0)
    sock.sendall(b'<getProperties version="1.7"/>\n')
    out(f"CONNECTED {host}:{port}")
    parser = ET.XMLPullParser(events=("start", "end"))
    parser.feed("<stream>")
    depth = 0
    last_coord = {}
    while time.time() < deadline:
        try:
            data = sock.recv(65536)
        except socket.timeout:
            continue
        if not data:
            raise OSError("connection closed")
        parser.feed(data.decode("utf-8", errors="replace"))
        for event, elem in parser.read_events():
            if event == "start":
                depth += 1
                continue
            depth -= 1
            if depth != 1:
                continue
            handle(elem, last_coord)
            elem.clear()


def handle(elem, last_coord):
    tag = elem.tag
    if tag not in ("setNumberVector", "defNumberVector"):
        return
    dev = elem.get("device", "?")
    name = elem.get("name", "?")
    state = elem.get("state", "?")
    values = {c.get("name"): (c.text or "").strip() for c in elem}
    if name in PULSE_VECTORS:
        vals = " ".join(f"{k}={v}" for k, v in values.items())
        out(f"PULSE {dev} {name} {state} {vals} ({tag})")
    elif name == "EQUATORIAL_EOD_COORD":
        now = time.time()
        if now - last_coord.get(dev, 0) >= 5 or tag == "defNumberVector":
            last_coord[dev] = now
            out(f"COORD {dev} {state} RA={values.get('RA')} DEC={values.get('DEC')}")
    elif name == "CCD_EXPOSURE":
        out(f"EXPOSURE {dev} {state} {values.get('CCD_EXPOSURE_VALUE')}")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
