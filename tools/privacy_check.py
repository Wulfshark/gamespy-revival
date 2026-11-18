#!/usr/bin/env python3
"""Privacy check: run before every push or release zip.

Fails if any file (text or binary) contains things from a developer's own machine:
personal user names, user-profile or drive paths, build-folder paths, or real IP
addresses. Generic defaults (C:\\Games\\..., C:\\Program Files, 127.0.0.1, documentation
example addresses) are allowed.

Usage: python3 tools/privacy_check.py [folder-or-zip ...]   (default: the repo)
"""
import os, re, sys, zipfile, ipaddress

BLOCK = [
    rb"(?i)\bwarsh\b",                       # developer's Windows user name
    rb"(?i)[a-z]:\\users\\[^\\\s\"']+",       # C:\Users\someone
    rb"(?i)/home/[a-z0-9_.-]+/",              # Linux home folders (build machines)
    rb"(?i)/Users/[a-z0-9_.-]+/",             # macOS home folders
    rb"(?i)[d-z]:\\(game installs|steamlibrary)",  # this PC's own drives and folders
    rb"(?i)\b(?!mygroup\.|yourname\.|example\.)[a-z0-9-]+\.duckdns\.org",  # a real dynamic-DNS name (placeholders allowed)
]
ALLOWED_IPS = {"127.0.0.1", "0.0.0.0", "255.255.255.255", "10.255.255.255"}
IP = re.compile(rb"(?<![\d.])(\d{1,3}(?:\.\d{1,3}){3})(?![\d.])")

def ip_ok(s, ctx):
    try:
        ip = ipaddress.ip_address(s)
    except ValueError:
        return True
    if s in ALLOWED_IPS or ip.is_loopback or ip in ipaddress.ip_network("203.0.113.0/24") \
            or ip in ipaddress.ip_network("198.51.100.0/24") or ip in ipaddress.ip_network("192.0.2.0/24"):
        return True
    # certificate OIDs and version numbers inside binaries, e.g. "2.5.4.3"
    if re.search(rb"\d\.\d\.\d\.\d\.\d", ctx) or s.startswith(("1.", "2.")) \
            or all(int(x) < 10 for x in s.split(".")):
        return True
    return False

def scan(name, data, problems):
    for pat in BLOCK:
        for m in re.finditer(pat, data):
            problems.append(f"{name}: {m.group(0)[:60].decode('latin1')}")
    for m in IP.finditer(data):
        s = m.group(1).decode()
        ctx = data[max(0, m.start() - 4):m.end() + 4]
        if not ip_ok(s, ctx):
            problems.append(f"{name}: IP address {s}")

def walk(target, problems):
    if zipfile.is_zipfile(target) and target.endswith(".zip"):
        with zipfile.ZipFile(target) as z:
            for n in z.namelist():
                if not n.endswith("/"):
                    scan(f"{target}!{n}", z.read(n), problems)
        return
    if os.path.isfile(target):
        with open(target, "rb") as f:
            scan(target, f.read(), problems)
        return
    for d, dirs, files in os.walk(target):
        dirs[:] = [x for x in dirs if x != ".git"]
        for f in files:
            p = os.path.join(d, f)
            if os.path.abspath(p) == os.path.abspath(__file__):
                continue
            walk(p, problems)

if __name__ == "__main__":
    targets = sys.argv[1:] or [os.path.dirname(os.path.dirname(os.path.abspath(__file__)))]
    problems = []
    for t in targets:
        walk(t, problems)
    if problems:
        print("PRIVACY CHECK FAILED:")
        for p in sorted(set(problems)):
            print("  " + p)
        sys.exit(1)
    print("Privacy check passed: no personal names, paths or IP addresses found.")
