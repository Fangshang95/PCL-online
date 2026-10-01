#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把一版更新资产发布到自建服务器，使其成为完整的文件级增量更新源。

【已废弃 · v50.10.3 起】客户端更新只认 GitHub 一个源，不再同步到自建服务器（/v1/update/* 路由下线）。
保留本脚本仅供历史回滚查询用，新版本不要用。

服务器的 /v1/update/* 路由直接按文件名从 /opt/tunnel-api/update/ 取文件，
所以只要把 version.json / version.json.sig / app.zip / patch-*.zip 放进去即可，
不需要改服务端代码。

两个硬约束：
  1. version.json 必须原样落盘——清单带 ECDSA 签名，服务端也保证原样返回，
     任何中间环节改动字节都会导致客户端验签失败。
  2. 只在"服务器真闲"时供货（在线/房间/带宽/日流量任一超阈值就 503），
     客户端会跳过这个源去试下一个，所以服务器永远不是唯一来源。

用法：
    python publish_server_update.py --dir D:\\PClonline\\dist\\v50.10.0\\update
    python publish_server_update.py --dir <dir> --host root@1.2.3.4 --key C:\\path\\key.pem
"""
import argparse
import hashlib
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request

SSH = r"C:\Windows\System32\OpenSSH\ssh.exe"
SCP = r"C:\Windows\System32\OpenSSH\scp.exe"

# 连接信息一律不写死在仓库里：优先命令行参数，其次环境变量，
# 再次 secrets/deploy.json（不入库）。仓库公开后不会泄露服务器地址与私钥路径。
DEPLOY_FILE = r"D:\PClonline\secrets\deploy.json"
DEF_REMOTE = "/opt/tunnel-api/update"
DEF_HTTP_PORT = 8801


def _load_deploy():
    try:
        with open(DEPLOY_FILE, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def die(msg):
    sys.exit("错误：" + msg)


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def sh(args, check=True):
    r = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if check and r.returncode != 0:
        die("命令失败（%d）：%s\n%s" % (r.returncode, " ".join(args[:4]), r.stderr[-600:]))
    return r


def http_probe(base, name):
    """只取响应头就断开，避免真下载 90 MB。返回 (状态码, Content-Length)。"""
    url = "%s/%s" % (base, name)
    try:
        req = urllib.request.Request(url, method="GET")
        req.add_header("User-Agent", "pclonline-publish")
        with urllib.request.urlopen(req, timeout=30) as r:
            r.read(1024)
            return r.status, r.headers.get("Content-Length")
    except urllib.error.HTTPError as e:
        return e.code, None
    except Exception as e:
        return "EXC:" + str(e)[:60], None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True, help="待上传资产所在目录")
    ap.add_argument("--host", default="", help="SSH 目标，如 root@1.2.3.4")
    ap.add_argument("--key", default="", help="SSH 私钥路径")
    ap.add_argument("--remote", default=DEF_REMOTE)
    ap.add_argument("--port", default="", help="更新服务端口，仅用于上传后的可达性自检")
    a = ap.parse_args()

    cfg = _load_deploy()
    host = a.host or os.environ.get("PCL_DEPLOY_HOST", "") or cfg.get("host", "")
    key = a.key or os.environ.get("PCL_DEPLOY_KEY", "") or cfg.get("key", "")
    port = str(a.port or os.environ.get("PCL_DEPLOY_PORT", "") or cfg.get("port", DEF_HTTP_PORT))
    if not host:
        die("未提供 SSH 目标：用 --host root@x.x.x.x，或设环境变量 PCL_DEPLOY_HOST，"
            "或写到 %s（{\"host\": \"...\", \"key\": \"...\"}）" % DEPLOY_FILE)
    if not key:
        die("未提供 SSH 私钥：用 --key <路径>，或设环境变量 PCL_DEPLOY_KEY")
    a.host, a.key = host, key
    # 自检用的 HTTP 基址从 host 推出来，不在代码里写死服务器地址
    http_base = "http://%s:%s/v1/update" % (host.split("@")[-1], port)

    d = a.dir
    if not os.path.isdir(d):
        die("资产目录不存在：" + d)
    files = [n for n in sorted(os.listdir(d)) if os.path.isfile(os.path.join(d, n))]
    # 说明文本不必上传
    files = [n for n in files if not n.endswith("upload-notes.txt")]
    if not files:
        die("资产目录为空：" + d)
    if "version.json" not in files:
        die("缺少 version.json")
    if "version.json.sig" not in files:
        print("警告：缺少 version.json.sig——客户端会按未签名处理（过渡期可容忍，但不推荐）")

    if not os.path.isfile(a.key):
        die("SSH 私钥不存在：" + a.key)

    ssh_base = [SSH, "-i", a.key, "-o", "StrictHostKeyChecking=no", a.host]
    scp_base = [SCP, "-i", a.key, "-o", "StrictHostKeyChecking=no"]

    sh(ssh_base + ["mkdir -p %s && echo ok" % a.remote])
    print("远端目录就绪：%s:%s" % (a.host, a.remote))

    for name in files:
        local = os.path.join(d, name)
        size = os.path.getsize(local)
        want = sha256_of(local)
        tmp = "%s/%s.uploading" % (a.remote, name)
        print("上传 %s（%.1f MB）…" % (name, size / 1048576.0), flush=True)
        sh(scp_base + [local, "%s:%s" % (a.host, tmp)])
        # 落位：先 mv 到位再校验，避免半截文件被客户端取到
        sh(ssh_base + ["mv -f '%s' '%s/%s' && sha256sum '%s/%s'" % (tmp, a.remote, name, a.remote, name)])
        out = sh(ssh_base + ["sha256sum '%s/%s'" % (a.remote, name)]).stdout.split()
        got = out[0] if out else ""
        if got.lower() != want:
            die("%s 校验不一致：本地 %s / 远端 %s" % (name, want, got))
        print("  校验一致 %s" % want[:16])

    print("--- 服务端可达性自检（可能返回 503：闲时供货闸门，属正常）---")
    for name in ["version.json", "version.json.sig", "app.zip"]:
        if name in files:
            code, clen = http_probe(http_base, name)
            print("  %-18s -> %s  %s" % (name, code, ("Content-Length=" + str(clen)) if clen else ""))
    print("清单地址：%s/manifest" % http_base)
    return 0


if __name__ == "__main__":
    sys.exit(main())
