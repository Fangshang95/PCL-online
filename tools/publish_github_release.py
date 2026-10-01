#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把一版更新资产发布到 GitHub Releases，使 GitHub 成为完整的文件级增量更新源。

客户端取清单的地址是
    https://github.com/<owner>/<repo>/releases/latest/download/version.json
它只对「最新的、非草稿、非预发布」的 Release 生效，所以本脚本建 Release 时
draft/prerelease 一律为 false，否则客户端必然 404。

清单里的包地址是相对文件名，客户端会按"清单取自哪个地址"拼出
    https://github.com/<owner>/<repo>/releases/latest/download/app.zip
所以只要这些文件都是同一个 Release 的资产，GitHub 源就能独立完成全量与增量更新。

用法：
    python publish_github_release.py --repo <owner>/<repo> --tag v50.10.0 ^
        --dir D:\\PClonline\\dist\\v50.10.0\\update [--create-repo] [--title "..."]

凭据：
    从 D:\\PClonline\\secrets\\github_token 读取（也支持环境变量 GITHUB_TOKEN）。
    脚本任何情况下都不会打印 token 内容。
"""
import argparse
import io
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

API = "https://api.github.com"
UPLOAD = "https://uploads.github.com"
TOKEN_FILE = r"D:\PClonline\secrets\github_token"
REPO_FILE = r"D:\PClonline\secrets\github_repo"

CTYPES = {
    ".zip": "application/zip",
    ".json": "application/json; charset=utf-8",
    ".sig": "text/plain; charset=utf-8",
    ".txt": "text/plain; charset=utf-8",
}


def nfc(name):
    """Release 资产名只接受 ASCII：中文名会被 GitHub 改写成 default.txt 或直接 422。"""
    try:
        name.encode("ascii")
        return True
    except UnicodeEncodeError:
        return False


def die(msg):
    sys.exit("错误：" + msg)


def read_token():
    tok = os.environ.get("GITHUB_TOKEN", "").strip()
    if not tok and os.path.isfile(TOKEN_FILE):
        tok = io.open(TOKEN_FILE, encoding="utf-8").read().strip()
    if not tok:
        die("未找到 GitHub 凭据。请把 fine-grained PAT 写入 %s（权限：Contents 读写，"
            "若界面有 Releases 项也勾上），或设置环境变量 GITHUB_TOKEN。" % TOKEN_FILE)
    return tok


def api(tok, method, path, body=None, raw=None, host=API, ctype="application/json"):
    data = None
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    elif raw is not None:
        data = raw
    req = urllib.request.Request(host + path, data=data, method=method)
    req.add_header("Authorization", "Bearer " + tok)
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("X-GitHub-Api-Version", "2022-11-28")
    req.add_header("User-Agent", "pclonline-publish")
    if data is not None:
        req.add_header("Content-Type", ctype)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            txt = r.read()
            return r.status, (json.loads(txt) if txt else {})
    except urllib.error.HTTPError as e:
        txt = e.read()
        try:
            return e.code, json.loads(txt) if txt else {}
        except Exception:
            return e.code, {"message": txt[:300].decode("utf-8", "replace")}


def ensure_repo(tok, repo, create):
    code, info = api(tok, "GET", "/repos/" + repo)
    if code == 200:
        print("仓库已存在：%s（private=%s）" % (repo, info.get("private")))
        if info.get("private"):
            print("  警告：私有仓库的 release 资产下载需要鉴权，匿名客户端取不到——请改成 Public")
        return info
    if code != 404:
        die("查询仓库失败 %s：%s" % (code, info.get("message")))
    if not create:
        die("仓库 %s 不存在（%s）。加 --create-repo 由脚本创建，或先在网页建好。" % (repo, code))
    owner, name = repo.split("/", 1)
    # 个人账号用 /user/repos；若 token 属于组织，改成 /orgs/<owner>/repos
    code, info = api(tok, "POST", "/user/repos",
                     {"name": name, "private": False, "auto_init": True,
                      "description": "PClonline 启动器更新分发"})
    if code not in (200, 201):
        die("创建仓库失败 %s：%s" % (code, info.get("message")))
    print("仓库已创建：%s" % info.get("html_url"))
    return info


def ensure_release(tok, repo, tag, title, notes):
    code, rel = api(tok, "GET", "/repos/%s/releases/tags/%s" % (repo, urllib.parse.quote(tag)))
    if code == 200:
        if rel.get("draft") or rel.get("prerelease"):
            print("发现已存在的 Release 是草稿/预发布，正在改成正式发布…")
            code2, rel2 = api(tok, "PATCH", "/repos/%s/releases/%d" % (repo, rel["id"]),
                              {"draft": False, "prerelease": False})
            if code2 == 200:
                rel = rel2
            else:
                die("无法把 Release 改为正式发布 %s：%s" % (code2, rel2.get("message")))
        print("Release 已就绪：%s（draft=%s prerelease=%s）"
              % (rel.get("html_url"), rel.get("draft"), rel.get("prerelease")))
        return rel
    if code != 404:
        die("查询 Release 失败 %s：%s" % (code, rel.get("message")))
    code, rel = api(tok, "POST", "/repos/%s/releases" % repo,
                    {"tag_name": tag, "name": title or tag, "body": notes or "",
                     "draft": False, "prerelease": False})
    if code not in (200, 201):
        die("创建 Release 失败 %s：%s" % (code, rel.get("message")))
    print("Release 已创建：%s（非草稿、非预发布，latest/download 可用）" % rel.get("html_url"))
    return rel


def upload_asset(tok, repo, rel, path, max_try=3):
    name = os.path.basename(path)
    size = os.path.getsize(path)
    ctype = CTYPES.get(os.path.splitext(name)[1].lower(), "application/octet-stream")
    url = "%s/repos/%s/releases/%d/assets?name=%s" % (
        UPLOAD, repo, rel["id"], urllib.parse.quote(name))
    for attempt in range(1, max_try + 1):
        try:
            with open(path, "rb") as f:
                body = f.read()
            code, info = api(tok, "POST", url[len(UPLOAD):], raw=body, host=UPLOAD, ctype=ctype)
            if code in (200, 201):
                print("  已上传 %s（%.1f MB）" % (name, size / 1048576.0))
                return True
            print("  第 %d 次上传 %s 失败 %s：%s" % (attempt, name, code, info.get("message")))
        except Exception as e:
            print("  第 %d 次上传 %s 异常：%s" % (attempt, name, str(e)[:200]))
        time.sleep(3 * attempt)
    return False


def verify(tok, repo, name):
    url = "https://github.com/%s/releases/latest/download/%s" % (repo, urllib.parse.quote(name))
    try:
        req = urllib.request.Request(url, method="GET")
        req.add_header("User-Agent", "pclonline-publish")
        with urllib.request.urlopen(req, timeout=60) as r:
            r.read(2048)
            return r.status
    except urllib.error.HTTPError as e:
        return e.code
    except Exception as e:
        return "EXC:" + str(e)[:80]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default="", help="owner/name")
    ap.add_argument("--tag", required=True, help="版本号，如 v50.10.0")
    ap.add_argument("--dir", required=True, help="待上传资产所在目录")
    ap.add_argument("--title", default="", help="Release 标题")
    ap.add_argument("--notes", default="", help="Release 说明")
    ap.add_argument("--create-repo", action="store_true", help="仓库不存在时自动创建（Public）")
    ap.add_argument("--skip-verify", action="store_true")
    a = ap.parse_args()

    repo = a.repo
    if not repo and os.path.isfile(REPO_FILE):
        repo = io.open(REPO_FILE, encoding="utf-8").read().strip()
    if not repo or "/" not in repo:
        die("需要 --repo <owner>/<repo>")

    d = a.dir
    if not os.path.isdir(d):
        die("资产目录不存在：" + d)
    assets = [os.path.join(d, n) for n in sorted(os.listdir(d)) if os.path.isfile(os.path.join(d, n))]
    # 非 ASCII 资产名会被 GitHub 做成 default.txt 或直接 422，一律本地保留不出包
    assets = [p for p in assets if nfc(os.path.basename(p))]
    if not assets:
        die("资产目录为空：" + d)
    print("待发布资产：%s" % ", ".join("%s(%.1fMB)" % (os.path.basename(p), os.path.getsize(p) / 1048576.0)
                                      for p in assets))

    tok = read_token()
    ensure_repo(tok, repo, a.create_repo)
    rel = ensure_release(tok, repo, a.tag, a.title, a.notes)

    have = {x["name"]: x.get("size", -1) for x in rel.get("assets", [])}
    todo = [p for p in assets
            if os.path.basename(p) not in have or have[os.path.basename(p)] != os.path.getsize(p)]
    for p in assets:
        n = os.path.basename(p)
        if p not in todo:
            print("  跳过 %s（Release 中已存在且大小一致）" % n)

    ok = True
    for p in todo:
        if not upload_asset(tok, repo, rel, p):
            ok = False

    if a.skip_verify:
        return 0 if ok else 1
    print("--- 校验 latest/download 可达性 ---")
    for p in assets:
        n = os.path.basename(p)
        print("  %s -> %s" % (n, verify(tok, repo, n)))
    print("清单地址：https://github.com/%s/releases/latest/download/version.json" % repo)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
