#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把本地某个标签推到远端（走云机中转，本机连不上 github.com）。

用法：python push_tag_via_cloud.py v50.11.0
"""
import io
import subprocess
import sys

REPO_DIR = r"D:\PClonline\pcl2-ce"
GIT = r"C:\Users\32157\.workbuddy\binaries\PortableGit\versions\1.2.0\cmd\git.exe"
SSH = r"C:\Windows\System32\OpenSSH\ssh.exe"
SSH_KEY = r"C:\Users\32157\Desktop\MC2.pem"
SSH_HOST = "root@120.26.198.92"
TOKEN_FILE = r"D:\PClonline\secrets\github_token"
REPO = "Fangshang95/PCL-online"

tag = sys.argv[1] if len(sys.argv) > 1 else sys.exit("用法：push_tag_via_cloud.py <tag>")
sha = subprocess.run([GIT, "-C", REPO_DIR, "rev-parse", tag + "^{commit}"],
                     capture_output=True, text=True).stdout.strip()
if not sha:
    sys.exit("本地没有这个标签：" + tag)
print("本地 %s → %s" % (tag, sha))

tok = io.open(TOKEN_FILE, encoding="utf-8").read().strip()
script = """TOK=$(cat /dev/stdin)
ok=0
for a in 1 2 3 4 5; do
  echo "== 云机第 $a 轮 =="
  rm -rf /tmp/tg
  git clone -q https://$TOK@github.com/%s.git /tmp/tg || { echo CLONE_FAIL; sleep 20; continue; }
  cd /tmp/tg
  git cat-file -e %s^{commit} 2>/dev/null || { echo NO_COMMIT; cd /; break; }
  if git push --force origin %s:refs/tags/%s; then ok=1; break; fi
  echo PUSH_FAIL; cd /; sleep 20
done
if [ $ok = 1 ]; then
  echo PUSH_OK
  echo "远端标签："
  git ls-remote -q origin refs/tags/%s
else
  echo ALL_FAIL
fi
rm -rf /tmp/tg
echo CLEANED""" % (REPO, sha, sha, tag, tag)

r = subprocess.run([SSH, "-i", SSH_KEY, "-o", "StrictHostKeyChecking=no", SSH_HOST, script],
                   input=tok, capture_output=True, text=True, encoding="utf-8", errors="replace")
print(r.stdout.strip()[:800])
if r.returncode != 0 or "PUSH_OK" not in r.stdout:
    sys.exit("标签推送失败：\n" + (r.stdout + r.stderr)[-800:])
