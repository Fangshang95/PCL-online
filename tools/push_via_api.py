#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""本机 github.com 被 DNS 指到 127.0.0.1 导致 git push 不通时，用 REST API 直接提交。

流程等价于一次正常 push：逐个建 blob → 建 tree → 建 commit（parent 为远端 main 当前
HEAD，因此是快进）→ 更新 refs/heads/main。blob 的 sha 会缓存到本地，中断可续跑。

用法：python push_via_api.py
"""
import base64
import io
import json
import os
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

REPO = "Fangshang95/PCL-online"
BRANCH = "main"
PARENT = "bf06fc6a5c603a8443beba4ae95239eed54d437f"  # 远端 Initial commit
REPO_DIR = r"D:\PClonline\pcl2-ce"
GIT = r"C:\Users\32157\.workbuddy\binaries\PortableGit\versions\1.2.0\cmd\git.exe"
TOKEN_FILE = r"D:\PClonline\secrets\github_token"
CACHE = os.path.join(REPO_DIR, ".tmp", "api_push_blobs.json")

MESSAGE = """PClonline v50.10.0（基于 PCL2-CE 的联机启动器）

- 联机包对齐：资源包/光影包/数据包随精确清单自动对齐，加入端自动装配兜底
- frpc 内嵌与自愈，防火墙/杀毒防护，管理员权限提醒
- v50.10 热更新：单 exe 首次运行自展开为 app 目录，按文件 sha256 做文件级增量，
  清单带 ECDSA 签名，替换失败自动回滚
- 更新源双通道：GitHub Releases 与自建服务器，任一可用即可完成增量"""

AUTHOR = {
    "name": "Fangshang95",
    "email": "335926345+Fangshang95@users.noreply.github.com",
}

TOKEN = io.open(TOKEN_FILE, encoding="utf-8").read().strip()
_lock = threading.Lock()
_cache = {}
_done = [0]


def api(method, path, body=None, tries=6):
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
    for i in range(tries):
        req = urllib.request.Request("https://api.github.com" + path, data=data, method=method)
        req.add_header("Authorization", "Bearer " + TOKEN)
        req.add_header("Accept", "application/vnd.github+json")
        req.add_header("X-GitHub-Api-Version", "2022-11-28")
        req.add_header("User-Agent", "pclonline-push")
        if data is not None:
            req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=120) as r:
                return r.status, json.loads(r.read() or b"{}")
        except urllib.error.HTTPError as e:
            raw = e.read()
            try:
                js = json.loads(raw or b"{}")
            except Exception:
                js = {"message": raw[:200].decode("utf-8", "replace")}
            # 429 / 5xx / 次级限流都退避重试
            if e.code in (403, 429, 500, 502, 503, 504) and i < tries - 1:
                wait = min(60, 4 * (i + 1))
                print("   %s %s -> %d，%ds 后重试" % (method, path[:40], e.code, wait), flush=True)
                time.sleep(wait)
                continue
            return e.code, js
        except Exception as e:
            if i < tries - 1:
                time.sleep(3 * (i + 1))
                continue
            return "EXC", {"message": str(e)[:200]}
    return "EXC", {"message": "重试耗尽"}


def load_cache():
    if os.path.isfile(CACHE):
        try:
            return json.load(io.open(CACHE, encoding="utf-8"))
        except Exception:
            return {}
    return {}


def save_cache():
    os.makedirs(os.path.dirname(CACHE), exist_ok=True)
    with _lock:
        json.dump(_cache, io.open(CACHE, "w", encoding="utf-8"))


def make_blob(rel):
    with _lock:
        if rel in _cache:
            return rel, _cache[rel]
    full = os.path.join(REPO_DIR, rel)
    data = open(full, "rb").read()
    st, js = api("POST", "/repos/%s/git/blobs" % REPO,
                 {"content": base64.b64encode(data).decode("ascii"), "encoding": "base64"})
    if st not in (200, 201):
        raise RuntimeError("blob 失败 %s：%s %s" % (rel, st, js.get("message")))
    with _lock:
        _cache[rel] = js["sha"]
        _done[0] += 1
        if _done[0] % 100 == 0:
            print("   已建 blob %d/%d" % (_done[0], len(_files)), flush=True)
            save_cache()
    return rel, js["sha"]


def main():
    global _files, _cache
    r = subprocess.run([GIT, "-C", REPO_DIR, "ls-files", "-z"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    _files = [f.replace("\\", "/") for f in r.stdout.split("\x00") if f]
    print("待提交文件：%d 个" % len(_files), flush=True)
    _cache = load_cache()
    print("已有缓存 blob：%d 个" % len(_cache), flush=True)

    pairs = []
    with ThreadPoolExecutor(max_workers=4) as ex:
        for rel, sha in ex.map(make_blob, _files):
            pairs.append((rel, sha))
    save_cache()
    print("blob 完成：%d 个" % len(pairs), flush=True)

    tree = [{"path": rel, "mode": "100644", "type": "blob", "sha": sha} for rel, sha in pairs]
    st, js = api("POST", "/repos/%s/git/trees" % REPO, {"tree": tree})
    if st not in (200, 201):
        sys.exit("建 tree 失败 %s：%s" % (st, js.get("message")))
    print("tree：%s" % js["sha"], flush=True)

    st, js = api("POST", "/repos/%s/git/commits" % REPO,
                 {"message": MESSAGE, "tree": js["sha"], "parents": [PARENT],
                  "author": AUTHOR, "committer": AUTHOR})
    if st not in (200, 201):
        sys.exit("建 commit 失败 %s：%s" % (st, js.get("message")))
    commit_sha = js["sha"]
    print("commit：%s" % commit_sha, flush=True)

    st, js = api("PATCH", "/repos/%s/git/refs/heads/%s" % (REPO, BRANCH), {"sha": commit_sha})
    if st != 200:
        sys.exit("更新 ref 失败 %s：%s" % (st, js.get("message")))
    print("已更新 refs/heads/%s -> %s" % (BRANCH, commit_sha), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
