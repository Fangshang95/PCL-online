#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
v50.10 热更新打包脚本

流程：
  1. 发布应用层（自包含，自带 .NET 运行时）→ build/app_layer
  2. 逐文件算 sha256，生成文件级清单（增量更新的依据）
  3. 压缩为 app.zip（全量包）+ 内嵌到引导器
  4. 与 baseline 里的各历史版本比对，为每个旧版本生成"只含变化文件"的增量包
  5. 写入 version.json（files / packages / remove）→ 用 ECDSA 私钥签名
  6. 发布引导器（单文件自包含）→ dist/PCLonline-alpha-<date>-<ver>.exe
  7. 产出可上传的发布资产到 dist/<version>/update/

「框架随包走、更新无框架」约定（v50.10.3）：
  · 全量包 app.zip 自包含，玩家第一次下载后 .NET 运行时就在 app\ 里就位，双击即用；
  · 增量包只打「sha256 变化的文件」——运行时文件不变，自然一个都不进包，
    所以后续更新永远是无框架版（本次实测：全量 60MB 级、增量 9.9MB 级）；
  · 引导器里保留了「发现应用层声明运行时就自动下载安装」的兜底（RuntimeInstaller），
    将来哪天改发无框架应用层，老机器也能自愈。

用法：
  python build_update.py v50.10.3              # 正常打包
  python build_update.py v50.10.3 --reuse-app  # 复用已发布的应用层（只重建包/清单）
  python build_update.py v50.10.3 --no-patch   # 不为历史版本生成增量包
"""
import ctypes
import datetime
import hashlib
import json
import os
import subprocess
import sys
import zipfile

DOTNET = r"D:\PClonline\dotnet-sdk\dotnet.exe"
PROJ = r"D:\PClonline\pcl2-ce\Plain Craft Launcher 2\Plain Craft Launcher 2.csproj"
BOOT_DIR = r"D:\PClonline\pcl2-ce\PClonlineBootstrap"
BOOT_PROJ = os.path.join(BOOT_DIR, "PClonlineBootstrap.csproj")
BUILD = r"D:\PClonline\build"
APP_DIR = os.path.join(BUILD, "app_layer")
DIST = r"D:\PClonline\dist"
BASELINE = r"D:\PClonline\baseline"
SECRETS = r"D:\PClonline\secrets"
SIGN_TOOL = os.path.join(BUILD, "sign", "PClonlineSign.dll")
PRIV_KEY = os.path.join(SECRETS, "update_sign_key.pem")

# 发布资产的基础 URL：GitHub Releases 用 latest，这样客户端不用跟着版本号改。
# 仓库名从 secrets/github_repo 读（一行 owner/name），换号不用改代码。
GH_REPO_FILE = os.path.join(SECRETS, "github_repo")
GH_REPO = "Fangshang95/PCL-online"
if os.path.isfile(GH_REPO_FILE):
    _s = open(GH_REPO_FILE, encoding="utf-8").read().strip()
    if _s:
        GH_REPO = _s
GH_BASE = "https://github.com/%s/releases/latest/download" % GH_REPO

# 更新分发**只走 GitHub**（客户端只有一个清单源）。
# 自建更新服务器（/v1/update/*）已下线，不再需要 deploy.json / publish_server_update.py。

ENV = dict(os.environ)
ENV["DOTNET_ROOT"] = r"D:\PClonline\dotnet-sdk"
ENV["NUGET_PACKAGES"] = r"D:\PClonline\nuget-packages"


def run(cmd, logfile=None):
    print(">", cmd, flush=True)
    if logfile:
        with open(logfile, "w", encoding="utf-8", errors="replace") as lf:
            r = subprocess.run(cmd, shell=True, env=ENV, stdout=lf, stderr=subprocess.STDOUT)
    else:
        r = subprocess.run(cmd, shell=True, env=ENV)
    if r.returncode != 0:
        sys.exit("命令失败（退出码 %d）：%s" % (r.returncode, cmd))


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def scan_files(root):
    """返回 [(rel, abs, size, sha256)]，按 rel 排序；跳过 version.json（清单自身）。"""
    out = []
    for dirpath, _, files in os.walk(root):
        for name in files:
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, root).replace("\\", "/")
            if rel == "version.json":
                continue
            out.append({"path": rel, "size": os.path.getsize(full), "sha256": sha256_of(full)})
    out.sort(key=lambda e: e["path"])
    return out


def zip_files(zpath, root, rels):
    """把 root 下的 rels 打成 zip（deflate/6）。返回 zip 大小。"""
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for rel in rels:
            zf.write(os.path.join(root, rel.replace("/", os.sep)), rel)
    return os.path.getsize(zpath)


def diff_against(old_idx, entries):
    """与某个旧版本比对：返回 (变化了的文件, 新版本已删除的文件)。

    只按 sha256 判定——所以只需要旧版本的清单，不需要保留旧版本的完整文件，
    baseline 目录因此只有几十 KB。
    """
    new_idx = {e["path"]: e["sha256"] for e in entries}
    changed = [e["path"] for e in entries if old_idx.get(e["path"]) != e["sha256"]]
    removed = sorted(p for p in old_idx if p not in new_idx)
    return changed, removed


def load_baselines(current):
    """读取历史版本清单，返回 {version: {path: sha256}}（不含当前版本）。"""
    out = {}
    if not os.path.isdir(BASELINE):
        return out
    for name in sorted(os.listdir(BASELINE)):
        if not (name.startswith("version-") and name.endswith(".json")):
            continue
        ver = name[len("version-"):-len(".json")]
        if ver == current:
            continue
        try:
            with open(os.path.join(BASELINE, name), encoding="utf-8") as f:
                m = json.load(f)
            out[ver] = {e["path"]: e["sha256"] for e in m.get("files", [])}
        except Exception as e:
            print("  跳过损坏的基线 %s：%s" % (name, e), flush=True)
    return out


def main():
    version = sys.argv[1] if len(sys.argv) > 1 else "v50.10.0"
    reuse = "--reuse-app" in sys.argv
    want_patch = "--no-patch" not in sys.argv
    today = datetime.date.today().strftime("%Y%m%d")
    print("=== 打包 %s ===" % version, flush=True)

    if reuse and os.path.isdir(APP_DIR):
        print("复用已发布的应用层：" + APP_DIR, flush=True)
    else:
        # 1. 发布应用层（自包含 + 非单文件，运行时随全量包到位、逐文件可增量更新）
        run('"%s" publish "%s" -c Release -p:Platform=x64 -p:SelfContained=true '
            '-p:PublishSingleFile=false -o "%s" --nologo -v q' % (DOTNET, PROJ, APP_DIR),
            os.path.join(BUILD, "publish_app.log"))

    # 2. 文件级 sha256 清单
    entries = scan_files(APP_DIR)
    total = sum(e["size"] for e in entries)
    print("清单：%d 个文件，共 %.1f MB" % (len(entries), total / 1048576), flush=True)
    new_idx = {e["path"]: e["sha256"] for e in entries}

    out_dir = os.path.join(DIST, version)
    upd_dir = os.path.join(out_dir, "update")
    os.makedirs(upd_dir, exist_ok=True)

    # 3a. 先写一份"仅文件清单"的 version.json 内嵌进 exe：
    #     自展开后 app\version.json 必须存在，客户端才认得出本地版本、才选得到增量包
    mpath = os.path.join(APP_DIR, "version.json")
    with open(mpath, "w", encoding="utf-8") as f:
        json.dump({"version": version,
                   "generated": datetime.datetime.now().isoformat(timespec="seconds"),
                   "files": entries}, f, ensure_ascii=False, indent=1)
    embed_zip = os.path.join(BUILD, "app.zip")
    zip_files(embed_zip, APP_DIR, [e["path"] for e in entries] + ["version.json"])

    # 3b. 全量包（对外发布用；不含清单自身，落地后由客户端写版本）
    full_zip = os.path.join(upd_dir, "app.zip")
    full_size = zip_files(full_zip, APP_DIR, [e["path"] for e in entries])
    print("全量包：%.1f MB" % (full_size / 1048576), flush=True)

    # 包地址一律写相对文件名：同一份清单放到 GitHub Releases 或自建服务器都成立，
    # 客户端按"清单取自哪个地址"解析成绝对 URL；服务器因此可以原样返回文件，不破坏签名
    packages = [{
        "type": "full", "from": "",
        "url": "app.zip",
        "size": full_size, "sha256": sha256_of(full_zip),
    }]

    # 4. 为每个历史版本生成"只含变化文件"的增量包
    old_versions = load_baselines(version)
    if want_patch and old_versions:
        for oldver, old_idx in sorted(old_versions.items()):
            changed, removed = diff_against(old_idx, entries)
            if not changed and not removed:
                print("  %s → %s：无变化，跳过" % (oldver, version), flush=True)
                continue
            pname = "patch-%s-to-%s.zip" % (oldver, version)
            ppath = os.path.join(upd_dir, pname)
            psize = zip_files(ppath, APP_DIR, changed)
            packages.append({
                "type": "patch", "from": oldver,
                "url": pname,
                "size": psize, "sha256": sha256_of(ppath),
                "remove": removed,
            })
            print("  %s → %s：变化 %d 个 / 删除 %d 个，增量包 %.1f MB（省 %.0f%%）"
                  % (oldver, version, len(changed), len(removed), psize / 1048576,
                     (1 - psize / full_size) * 100), flush=True)
    else:
        print("未生成增量包（无历史基线或已禁用）", flush=True)

    # 5. version.json + 签名
    manifest = {
        "version": version,
        "generated": datetime.datetime.now().isoformat(timespec="seconds"),
        "files": entries,
        "packages": packages,
    }
    mpath = os.path.join(APP_DIR, "version.json")
    with open(mpath, "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=1)
    ctypes.windll.kernel32.CopyFileW(mpath, os.path.join(upd_dir, "version.json"), False)

    if os.path.isfile(PRIV_KEY) and os.path.isfile(SIGN_TOOL):
        run('"%s" "%s" sign "%s" "%s" "%s"' % (
            DOTNET, SIGN_TOOL, PRIV_KEY,
            os.path.join(upd_dir, "version.json"),
            os.path.join(upd_dir, "version.json.sig")),
            os.path.join(BUILD, "sign.log"))
        run('"%s" "%s" verify "%s" "%s" "%s"' % (
            DOTNET, SIGN_TOOL, os.path.join(SECRETS, "update_sign_pub.b64"),
            os.path.join(upd_dir, "version.json"),
            os.path.join(upd_dir, "version.json.sig")),
            os.path.join(BUILD, "verify.log"))
        print("已签名并验签：version.json.sig", flush=True)
    else:
        print("警告：缺少私钥或签名工具，未签名（客户端会按未签名处理）", flush=True)

    # 保存基线，供下次生成增量包
    os.makedirs(BASELINE, exist_ok=True)
    with open(os.path.join(BASELINE, "version-%s.json" % version), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=1)

    # 6. 内嵌 app.zip 到引导器并发布
    boot_zip = os.path.join(BOOT_DIR, "app.zip")
    with open(boot_zip, "wb") as f:
        f.write(open(embed_zip, "rb").read())
    #   RuntimeIdentifier 必须显式传：csproj 里的 Condition 依赖 Platform，实测不生效
    boot_out = os.path.join(BUILD, "boot_out")
    run('"%s" publish "%s" -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 '
        '-p:SelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true '
        '-p:DeleteExistingFiles=true -o "%s" --nologo -v q' % (DOTNET, BOOT_PROJ, boot_out),
        os.path.join(BUILD, "publish_boot.log"))
    src = os.path.join(boot_out, "PClonline.exe")
    out = os.path.join(DIST, "PCLonline-alpha-%s-%s.exe" % (today, version))
    with open(out, "wb") as f:
        f.write(open(src, "rb").read())
    print("=== 完成 ===", flush=True)
    print("分发 exe：%s（%.1f MB）" % (out, os.path.getsize(out) / 1048576), flush=True)
    print("sha256：%s" % sha256_of(out), flush=True)
    print("发布资产：%s" % upd_dir, flush=True)

    # 7. 上传清单，方便手工拖到 GitHub Release / 传到服务器
    lines = [
        "发布资产（全部传到 GitHub Release 的 latest，更新只认这一个源）：",
        "",
        "  version.json",
        "  version.json.sig",
        "  app.zip            %.1f MB（全量兜底，无框架版）" % (full_size / 1048576),
    ]
    for p in packages:
        if p["type"] == "patch":
            lines.append("  patch-%s-to-%s.zip  %.1f MB（增量）" % (p["from"], version, p["size"] / 1048576))
    lines += [
        "",
        "清单里的包地址是相对文件名，客户端按清单来源自动拼成绝对地址：",
        "  GitHub  → " + GH_BASE + "/app.zip",
        "",
        "一键发布：",
        "  python tools\\publish_github_release.py --tag " + version + " --dir " + upd_dir,
        "",
        "上传要求：",
        "  · 这些资产必须都在同一个 latest Release 里（latest/download 前缀要求）",
        "  · version.json 与 version.json.sig 必须成对更新（签名针对文件原始字节，",
        "    Release 会原样返回，任何改动都会导致验签失败）",
        "  · app.zip 自包含（自带 .NET 运行时），所以玩家首次下载即可用，不需要另装运行库",
    ]
    # 资产名必须是 ASCII，否则 GitHub Release 会 422 或改名成 default.txt
    with open(os.path.join(upd_dir, "upload-notes.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    # 清理引导器项目里的临时 zip（避免误提交进 git）
    if os.path.exists(boot_zip):
        ctypes.windll.kernel32.DeleteFileW(boot_zip)


if __name__ == "__main__":
    main()
