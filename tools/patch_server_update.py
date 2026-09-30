#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""给线上 tunnel_server.py 打补丁：新增 /v1/update/* 热更新分发路由（闲时供货）。

幂等：已打过补丁（含 UPDATE_DIR 定义）则直接跳过。
用法：python patch_server_update.py <输入文件> <输出文件>
"""
import io
import sys

SRC = sys.argv[1] if len(sys.argv) > 1 else r"D:\PClonline\live_tunnel_server.py"
DST = sys.argv[2] if len(sys.argv) > 2 else r"D:\PClonline\live_tunnel_server_patched.py"

src = io.open(SRC, encoding="utf-8").read()

if "PCL_UPDATE_DIR" in src:
    io.open(DST, "w", encoding="utf-8").write(src)
    print("已包含更新路由，原样输出")
    sys.exit(0)

MODULE_BLOCK = '''
# ---------- v50.10 热更新分发：云机出网带宽极窄，只在"真闲"时供货 ----------
UPDATE_DIR = _p("PCL_UPDATE_DIR", "/opt/tunnel-api/update")
UPDATE_MAX_ONLINE = int(os.environ.get("PCL_UPDATE_MAX_ONLINE", "5"))
UPDATE_MAX_ROOMS = int(os.environ.get("PCL_UPDATE_MAX_ROOMS", "3"))
UPDATE_MAX_TX_RATE = float(os.environ.get("PCL_UPDATE_MAX_TX_RATE", "600000"))  # B/s
UPDATE_DAY_BYTES = int(os.environ.get("PCL_UPDATE_DAY_BYTES", str(3 * 1024 * 1024 * 1024)))

_UPDATE_TX = {"t": 0.0, "tx": 0, "rate": 0.0}
_UPDATE_DAY = {"day": "", "bytes": 0}
_UPDATE_LK = threading.Lock()


def net_tx_rate():
    """采样 /proc/net/dev 出网字节速率（含 frp 中转流量），指数平滑，单位 B/s。"""
    try:
        now = time.time()
        tx = 0
        with open("/proc/net/dev", "r") as f:
            for line in f.read().splitlines()[2:]:
                if ":" not in line:
                    continue
                iface, rest = line.split(":", 1)
                if iface.strip() == "lo":
                    continue
                cols = rest.split()
                if len(cols) > 8:
                    tx += int(cols[8])
        prev = _UPDATE_TX
        if prev["t"] and now > prev["t"] and tx >= prev["tx"]:
            inst = (tx - prev["tx"]) / (now - prev["t"])
            prev["rate"] = prev["rate"] * 0.5 + inst * 0.5 if prev["rate"] else inst
        prev["t"], prev["tx"] = now, tx
        return prev["rate"]
    except Exception:
        return 0.0


def online_account_count():
    """最近 _ONLINE_WINDOW 秒内有 session 活动的账号数。"""
    now = int(time.time())
    try:
        with db() as c:
            row = c.execute(
                "SELECT COUNT(*) AS n FROM (SELECT DISTINCT s.email FROM sessions s "
                "WHERE s.expires_at > ? AND s.last_seen > ?)",
                (now, now - _ONLINE_WINDOW)).fetchone()
            return int(row["n"] or 0)
    except Exception:
        return -1


def active_room_count():
    with LOCK:
        s = load_state()
        reap_expired(s)
        return len(s.get("rooms", {}) or {})


def update_asset_names():
    try:
        return set(os.listdir(str(UPDATE_DIR)))
    except Exception:
        return set()


def update_gate():
    """是否允许分发更新，返回 (ok, reason, info)。任一条件不满足就暂停供货，
    客户端收到非 200 会退回 GitHub Releases——服务器永远不是唯一来源。"""
    online = online_account_count()
    rooms = active_room_count()
    rate = net_tx_rate()
    with _UPDATE_LK:
        day = time.strftime("%Y%m%d")
        if _UPDATE_DAY["day"] != day:
            _UPDATE_DAY["day"], _UPDATE_DAY["bytes"] = day, 0
        sent = _UPDATE_DAY["bytes"]
    info = {"online": online, "rooms": rooms, "tx_rate": int(rate),
            "sent_today": sent, "max_online": UPDATE_MAX_ONLINE,
            "max_rooms": UPDATE_MAX_ROOMS, "max_tx_rate": int(UPDATE_MAX_TX_RATE),
            "day_budget": UPDATE_DAY_BYTES}
    if not os.path.isdir(str(UPDATE_DIR)):
        return False, "update dir not configured", info
    if online >= UPDATE_MAX_ONLINE:
        return False, "too many players online", info
    if rooms >= UPDATE_MAX_ROOMS:
        return False, "rooms active", info
    if rate >= UPDATE_MAX_TX_RATE:
        return False, "bandwidth busy", info
    if sent >= UPDATE_DAY_BYTES:
        return False, "daily budget exhausted", info
    return True, "idle", info


def update_account_bytes(n):
    with _UPDATE_LK:
        day = time.strftime("%Y%m%d")
        if _UPDATE_DAY["day"] != day:
            _UPDATE_DAY["day"], _UPDATE_DAY["bytes"] = day, 0
        _UPDATE_DAY["bytes"] += n


'''

HANDLER_BLOCK = '''    def _send_bytes(self, code, body, ctype="application/octet-stream"):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _send_file(self, code, path, ctype="application/octet-stream"):
        """分块流式下发大文件（全量包近 90 MB，不能一次性读进内存），
        并按实际发出的字节计入日流量配额。"""
        size = os.path.getsize(path)
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(size))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        sent = 0
        try:
            with open(path, "rb") as f:
                while True:
                    chunk = f.read(262144)
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    sent += len(chunk)
        except (BrokenPipeError, ConnectionResetError):
            pass  # 客户端中途取消，按已发出字节计费即可
        finally:
            update_account_bytes(sent)

    def _update_route(self):
        """热更新分发：manifest / 签名 / 资产。仅在服务器真闲时供货，否则 503
        让客户端退回 GitHub Releases（服务器是加速源，不是唯一来源）。"""
        tail = self.path.split("?", 1)[0][len("/v1/update/"):].strip("/")
        if not RL.allow("update:%s:min" % self._client_ip(), RL_LIMITS["update_min"]):
            return self._send(429, {"error": "rate limited"})
        ok, reason, info = update_gate()
        if not ok:
            return self._send(503, {"error": "update paused",
                                    "reason": reason, "info": info})
        base = str(UPDATE_DIR)
        if tail in ("manifest", "version.json"):
            p = os.path.join(base, "version.json")
            if not os.path.isfile(p):
                return self._send(404, {"error": "manifest missing"})
            # 原样返回，一个字节都不改：清单带 ECDSA 签名，改了就验不过。
            # 包地址是相对文件名，客户端会按"清单取自哪个地址"自己拼出
            # /v1/update/<name>，所以这里什么都不用做
            return self._send_bytes(200, open(p, "rb").read(),
                                    "application/json; charset=utf-8")
        if tail in ("version.json.sig", "manifest.sig"):
            p = os.path.join(base, "version.json.sig")
            if not os.path.isfile(p):
                return self._send(404, {"error": "signature missing"})
            return self._send_bytes(200, open(p, "rb").read(),
                                    "text/plain; charset=utf-8")
        # 资产下发：/v1/update/<name> 与 /v1/update/asset/<name> 都支持。
        # 客户端是把清单里的相对文件名拼在"清单取自哪个地址"后面的，所以裸文件名
        # 才是主路径；asset/ 前缀只为兼容早期写法保留。名字必须命中目录列举结果，
        # 且不含路径分隔符，避免 ../ 穿越。
        name = tail[len("asset/"):] if tail.startswith("asset/") else tail
        if name and "/" not in name and "\\\\" not in name and name not in (".", ".."):
            if name not in update_asset_names():
                return self._send(404, {"error": "asset not found"})
            p = os.path.join(base, name)
            if not os.path.isfile(p):
                return self._send(404, {"error": "asset missing"})
            return self._send_file(200, p,
                                   "application/zip" if name.lower().endswith(".zip")
                                   else "application/octet-stream")
        return self._send(404, {"error": "unknown update path"})

'''

ROUTE_BLOCK = '''        # v50.10 热更新分发（闲时供货；忙时 503 让客户端退回 GitHub Releases）
        if self.path.startswith("/v1/update/"):
            return self._update_route()
'''


def insert_before(text, anchor, block, what):
    if anchor not in text:
        sys.exit("找不到锚点（%s）：%s" % (what, anchor))
    return text.replace(anchor, block + anchor, 1)


out = src
out = insert_before(out, "class Handler(BaseHTTPRequestHandler):", MODULE_BLOCK, "Handler 类")
out = insert_before(out, "    def do_GET(self):", HANDLER_BLOCK, "do_GET")
out = insert_before(
    out,
    '        if self.path == "/v1/metrics/summary":',
    ROUTE_BLOCK, "metrics/summary 路由")
# 限流配额：更新清单/资产，同 IP 每分钟 30 次（正常每台机器每天只跑几次）
old_rl = '    "register_min": (60, 10),'
if old_rl in out:
    out = out.replace(old_rl, old_rl + '\n    "update_min": (60, 30),      # 同 IP 每分钟 30 次更新清单/资产请求', 1)
else:
    print("警告：未找到 RL_LIMITS 锚点，请手工补 update_min")

io.open(DST, "w", encoding="utf-8").write(out)
print("补丁完成：%s（%d → %d 字符）" % (DST, len(src), len(out)))
