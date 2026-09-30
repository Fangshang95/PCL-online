#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""修正 /v1/update/* 路由的资产下发：

1) 客户端把清单里的相对文件名按"清单取自哪个地址"拼成 /v1/update/<name>（没有
   asset/ 前缀），而服务端只认 asset/<name>，导致走服务器源下载全量包必然 404。
   现在两种写法都支持，裸文件名为主路径。
2) 全量包 90 MB，原实现 open().read() 一次性读进内存；改成 256 KB 分块流式下发，
   并如实计入日流量配额（客户端中断也按已发出字节计）。

幂等：已含 _send_file 则原样输出。
用法：python patch_server_update_asset.py <输入> <输出>
"""
import io
import sys

SRC = sys.argv[1] if len(sys.argv) > 1 else r"D:\PClonline\live\tunnel_server.py"
DST = sys.argv[2] if len(sys.argv) > 2 else r"D:\PClonline\live\tunnel_server_patched.py"

src = io.open(SRC, encoding="utf-8").read()

if "_send_file" in src:
    io.open(DST, "w", encoding="utf-8").write(src)
    print("已含流式资产下发，原样输出")
    sys.exit(0)

OLD = '''        if tail.startswith("asset/"):
            name = tail[len("asset/"):]
            if name not in update_asset_names():
                return self._send(404, {"error": "asset not found"})
            p = os.path.join(base, name)
            if not os.path.isfile(p):
                return self._send(404, {"error": "asset missing"})
            body = open(p, "rb").read()
            update_account_bytes(len(body))
            return self._send_bytes(200, body)
        return self._send(404, {"error": "unknown update path"})
'''

NEW = '''        # 资产下发：/v1/update/<name> 与 /v1/update/asset/<name> 都支持。
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

if OLD not in src:
    sys.exit("找不到待修正的资产分支，请确认输入文件")

out = src.replace(OLD, NEW, 1)

# 顺手订正旧注释里写错的客户端拼法（asset/ 前缀）
out = out.replace(
    '# /v1/update/asset/<name>，所以这里什么都不用做',
    '# /v1/update/<name>，所以这里什么都不用做', 1)

STREAM = '''    def _send_file(self, code, path, ctype="application/octet-stream"):
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

    def _update_route(self):'''

out = out.replace("    def _update_route(self):", STREAM, 1)

io.open(DST, "w", encoding="utf-8").write(out)
print("修正完成：%s（%d → %d 字符）" % (DST, len(src), len(out)))
