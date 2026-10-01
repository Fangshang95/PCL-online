#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""本机 github.com 被 DNS 指到 127.0.0.1 时的推送通道：云机中转。

本地打 bundle → scp 到云机 → 云机上 clone 远端仓库、把 bundle 的提交 rebase 到
远端 main 之上（-X theirs：同名文件以本地为准）→ 推回 main → 清理临时目录。
提交作者/提交者保持本地仓库的配置（Fangshang95 + GitHub no-reply 邮箱），
GitHub 上的提交历史就是一条正常的开发者时间线。

用法：python push_via_cloud.py ["提交说明（可选，若本地已有未提交改动则先提交）"]
"""
import io
import os
import subprocess
import sys

REPO_DIR = r"D:\PClonline\pcl2-ce"
GIT = r"C:\Users\32157\.workbuddy\binaries\PortableGit\versions\1.2.0\cmd\git.exe"
SSH = r"C:\Windows\System32\OpenSSH\ssh.exe"
SCP = r"C:\Windows\System32\OpenSSH\scp.exe"
SSH_KEY = r"C:\Users\32157\Desktop\MC2.pem"
SSH_HOST = "root@120.26.198.92"
BUNDLE_LOCAL = r"D:\PClonline\live\repo.bundle"
BUNDLE_REMOTE = "/tmp/repo.bundle"
TOKEN_FILE = r"D:\PClonline\secrets\github_token"
REPO = "Fangshang95/PCL-online"
BRANCH = "main"


def sh(args, check=True):
    r = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if check and r.returncode != 0:
        sys.exit("命令失败（%d）：%s\n%s" % (r.returncode, " ".join(args[:3]), (r.stdout + r.stderr)[-800:]))
    return (r.stdout + r.stderr).strip()


def main():
    # 1) 有未提交改动就先提交（默认模式：提交全部）
    status = sh([GIT, "-C", REPO_DIR, "status", "--porcelain=v1"], check=False)
    if status and len(sys.argv) > 1:
        sh([GIT, "-C", REPO_DIR, "add", "-A"])
        sh([GIT, "-C", REPO_DIR, "commit", "-m", sys.argv[1]])
    elif status:
        print("提示：工作区有未提交改动，但未提供提交说明，只推送已提交内容：")
        print("\n".join("   " + l for l in status.splitlines()[:10]))

    # 2) 本地 bundle
    if os.path.isfile(BUNDLE_LOCAL):
        os.remove(BUNDLE_LOCAL)
    os.makedirs(os.path.dirname(BUNDLE_LOCAL), exist_ok=True)
    print(sh([GIT, "-C", REPO_DIR, "bundle", "create", BUNDLE_LOCAL, BRANCH])[-200:])
    print("bundle %.1f MB" % (os.path.getsize(BUNDLE_LOCAL) / 1048576.0))

    # 3) 上传到云机
    sh([SCP, "-i", SSH_KEY, "-o", "StrictHostKeyChecking=no", BUNDLE_LOCAL,
        "%s:%s" % (SSH_HOST, BUNDLE_REMOTE)])
    print("bundle 已上传云机")

    # 4) 云机上：clone → 校验快进 → push
    tok = io.open(TOKEN_FILE, encoding="utf-8").read().strip()
    script = """set -e
TOK=$(cat /dev/stdin)
rm -rf /tmp/pr && git clone -q https://$TOK@github.com/%s.git /tmp/pr
cd /tmp/pr
git fetch -q %s %s:bundle-main
# 本地历史应包含远端 main（快进）；若不包含说明本地落后或分叉，直接报错让人工处理
git merge-base --is-ancestor origin/main bundle-main || {
  echo NOT_FAST_FORWARD; git log --oneline origin/main -3; exit 1; }
git push origin bundle-main:%s
echo PUSH_OK
git ls-remote -q origin refs/heads/%s
rm -rf /tmp/pr /tmp/repo.bundle
echo CLEANED""" % (REPO, BUNDLE_REMOTE, BRANCH, BRANCH, BRANCH)
    r = subprocess.run([SSH, "-i", SSH_KEY, "-o", "StrictHostKeyChecking=no", SSH_HOST, script],
                       input=tok, capture_output=True, text=True, encoding="utf-8", errors="replace")
    print(r.stdout.strip()[:600])
    if r.returncode != 0 or "PUSH_OK" not in r.stdout:
        sys.exit("云机推送失败：\n" + (r.stdout + r.stderr)[-800:])
    return 0


if __name__ == "__main__":
    sys.exit(main())
