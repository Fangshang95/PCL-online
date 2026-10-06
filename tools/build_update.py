#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
v50.11 热更新打包脚本（应用层自展开 + 运行环境由玩家自备）

结构（v50.11.7 起）：
  应用层 app\        —— exe 内嵌 app.zip 首跑自展开，每次更新全量覆盖
  运行环境           —— **不再随包分发**。玩家自行从微软官网下载安装
                       .NET Desktop Runtime 10（x64），启动器只检测 + 给下载链接

产出（放到同一个 GitHub Release 的 latest 下）：
  version.json     清单（ECDSA 签名）
  version.json.sig 签名
  app.zip          无框架应用层（framework-dependent，不含运行时）
  PCLonline-<ver>-offline.zip  exe + 使用说明 + 运行环境说明

v50.11.7 的取舍（回退 v50.11.6 的自动安装）：
  v50.11.6 会用包内 net.zip 申请 UAC 把运行时自动装进 %ProgramFiles%\dotnet。
  实测对玩家不可靠——提权可能被拒、杀软拦 UAC、弱网下 70MB 包下不动、
  老版本残留难清理；而且替玩家装系统组件超出了启动器该做的事。
  故改为 CE 原版做法：启动器只检测，缺环境就弹窗给官方下载链接。
  **不联网下载、不提权安装、不写任何系统目录。**

保留的两层结构：
  1. 首跑：exe 内嵌 app.zip → 自展开到 app\
  2. 更新：只下 app.zip 全量覆盖 app\，运行环境用户自己管
  → 「基线版本 / diff / remove 列表 / 多份 patch」这套复杂度全部消失

用法：
  python build_update.py v50.11.7
"""
import ctypes
import datetime
import hashlib
import json
import os
import re
import subprocess
import sys
import zipfile

DOTNET = r"D:\PClonline\dotnet-sdk\dotnet.exe"
SDK_DIR = r"D:\PClonline\dotnet-sdk"
PROJ = r"D:\PClonline\pcl2-ce\Plain Craft Launcher 2\Plain Craft Launcher 2.csproj"
BOOT_DIR = r"D:\PClonline\pcl2-ce\PClonlineBootstrap"
BOOT_PROJ = os.path.join(BOOT_DIR, "PClonlineBootstrap.csproj")
BUILD = r"D:\PClonline\build"
APP_DIR = os.path.join(BUILD, "app_layer")          # 应用层（无框架）
DIST = r"D:\PClonline\dist"
SECRETS = r"D:\PClonline\secrets"
SIGN_TOOL = os.path.join(BUILD, "sign", "PClonlineSign.dll")
PRIV_KEY = os.path.join(SECRETS, "update_sign_key.pem")

GH_REPO_FILE = os.path.join(SECRETS, "github_repo")
GH_REPO = "Fangshang95/PCL-online"
if os.path.isfile(GH_REPO_FILE):
    _s = open(GH_REPO_FILE, encoding="utf-8").read().strip()
    if _s:
        GH_REPO = _s
GH_BASE = "https://github.com/%s/releases/latest/download" % GH_REPO

# 更新分发只走 GitHub（客户端只有一个清单源）
ENV = dict(os.environ)
ENV["DOTNET_ROOT"] = SDK_DIR
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


def zip_files(zpath, roots):
    """roots: {root: [rel, ...]}，按 root 逐个写入 zip。返回 zip 大小。"""
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for root, rels in roots.items():
            for rel in rels:
                zf.write(os.path.join(root, rel.replace("/", os.sep)), rel)
    return os.path.getsize(zpath)


def read_frameworks(app_dir):
    """从应用层的 runtimeconfig.json 读出它要的 framework（name → 最低版本）。"""
    import glob
    target = None
    for cfg in glob.glob(os.path.join(app_dir, "*.runtimeconfig.json")):
        with open(cfg, encoding="utf-8") as f:
            doc = json.load(f)
        ro = doc.get("runtimeOptions") or doc
        # 命中即停：frameworks（数组）或 framework（单个）任一存在都算找到了声明
        if ro.get("frameworks") is not None or ro.get("framework") is not None:
            target = ro
            break
    out = []
    if not target:
        return out
    # frameworks 在新版 SDK 里是 [{"name":...,"version":...}, ...]，老格式是 {"name": {"version":...}}
    fw = target.get("frameworks")
    if isinstance(fw, list):
        for e in fw:
            if isinstance(e, dict) and e.get("name"):
                out.append((e["name"], e.get("version") or "0.0.0"))
    elif isinstance(fw, dict):
        for name, ver in fw.items():
            out.append((name, (ver or {}).get("version") or "0.0.0"))
    if not out and target.get("framework"):
        out.append((target["framework"].get("name"), target["framework"].get("version") or "0.0.0"))
    return out
def main():
    version = sys.argv[1] if len(sys.argv) > 1 else "v50.11.0"
    today = datetime.date.today().strftime("%Y%m%d")
    print("=== 打包 %s ===" % version, flush=True)

    # 1. 发布应用层（无框架：运行时不随包走，玩家要的分发形态 = 分离式）。
    #    v50.11.2~11.4 的"无框架 + 外挂运行时"在玩家机器连续翻车（net.zip 下不动、
    #    双击 app\ 内同名 exe 弹英文缺 .NET、提权重启丢 DOTNET_ROOT），v50.11.5 回归
    #    v50.10.3 的自包含形态：永远不需要联网装运行时。引导器更新机制保留。
    print("--- 发布应用层（自包含）---", flush=True)
    run('"%s" publish "%s" -c Release -p:Platform=x64 -p:SelfContained=false '
        '-p:PublishSingleFile=false -o "%s" --nologo -v q' % (DOTNET, PROJ, APP_DIR),
        os.path.join(BUILD, "publish_app.log"))

    # 1b. 先写应用层版本标记，再扫描/打 zip —— 顺序是铁律：
    #     APP_DIR\version.json 里上一版构建的残留清单如果不先盖掉，
    #     内嵌进 exe 的版本号就会永远慢一拍，玩家"换 exe 升级"后界面版本识别错误
    with open(os.path.join(APP_DIR, "version.json"), "w", encoding="utf-8") as f:
        json.dump({"version": version}, f)

    entries = scan_files(APP_DIR)
    total = sum(e["size"] for e in entries)
    print("应用层：%d 个文件，共 %.1f MB" % (len(entries), total / 1048576), flush=True)

    # 2. 运行环境说明文件（v50.11.7：不再随包分发 net.zip）
    #    运行环境改由玩家自己从微软官网下载安装（CE 原版做法），
    #    启动器缺环境时只弹窗给下载链接，不联网下载、不提权安装。
    #    包里只留一份中文说明 + 官方链接，让玩家知道去哪下。
    print("--- 生成运行环境说明 ---", flush=True)
    fw_list = read_frameworks(APP_DIR)
    if not fw_list:
        sys.exit("应用层 runtimeconfig 里没声明 framework，无法确定所需运行环境")
    print("  声明的框架：%s" % ", ".join("%s %s" % (n, v) for n, v in fw_list), flush=True)
    fw_pretty = "、".join("%s %s" % (n, v) for n, v in fw_list)

    out_dir = os.path.join(DIST, version)
    upd_dir = os.path.join(out_dir, "update")
    os.makedirs(upd_dir, exist_ok=True)

    # 3. 包
    all_rels = [e["path"] for e in entries]
    # 3a. 内嵌进 exe 的那份（带版本标记，自展开后 app\ 里就有版本标记）
    embed_zip = os.path.join(BUILD, "app.zip")
    zip_files(embed_zip, {APP_DIR: all_rels + ["version.json"]})
    # 3b. 对外发布的全量应用层
    full_zip = os.path.join(upd_dir, "app.zip")
    full_size = zip_files(full_zip, {APP_DIR: all_rels})
    print("app.zip：%.1f MB（无框架应用层）" % (full_size / 1048576), flush=True)
    # 3c. 运行环境说明文件（v50.11.7：替代原 net.zip，玩家照着它自己去官网下载）
    env_txt = os.path.join(upd_dir, "运行环境-请先安装.txt")
    with open(env_txt, "w", encoding="utf-8") as f:
        f.write(
            "PClonine 需要先安装 .NET 运行环境（一次性，之后永不再需要）\r\n"
            "\r\n"
            "【需要什么】\r\n"
            "%s\r\n"
            "即微软官方 .NET Desktop Runtime 10（64 位）。\r\n"
            "\r\n"
            "【去哪儿下】\r\n"
            "https://dotnet.microsoft.com/download/dotnet/10.0\r\n"
            "\r\n"
            "【怎么装】\r\n"
            "1) 打开上面的网址，页面拉到 “SDK”/“Runtime” 一栏；\r\n"
            "2) 点 “Windows Desktop Runtime”，选 x64 的 exe 下载（约 60MB）；\r\n"
            "3) 双击安装，一路点“下一步”即可，不需要改任何选项；\r\n"
            "4) 装完重新双击 PClonine.exe 就能启动。\r\n"
            "\r\n"
            "【说明】\r\n"
            "· 启动器不会再帮你自动下载或自动安装运行环境（避免 UAC 提权、\r\n"
            "  杀软拦截、弱网下不动等问题）；缺环境时它只会弹窗提示你这个链接。\r\n"
            "· 如果你用的是 Windows 11 或 Win10（较新版本），系统可能已经自带了，\r\n"
            "  直接双击 PClonine.exe 就能用，不会弹提示。\r\n" % fw_pretty
        )
    print("运行环境-请先安装.txt（%.0f KB）" % (os.path.getsize(env_txt) / 1024), flush=True)

    # 包地址一律写相对文件名：客户端按"清单取自哪个地址"拼绝对 URL，
    # 清单字节原样返回才不会破坏 ECDSA 签名
    packages = [{
        "type": "full", "from": "",
        "url": "app.zip",
        "size": full_size, "sha256": sha256_of(full_zip),
    }]

    # 4. version.json + 签名（只写到发布目录；APP_DIR\version.json 是 1b 写的简版标记，
    #    千万别用完整清单盖回去——下次构建又会把它当残留嵌进 exe）
    #    v50.11.7：不再有 runtime 段（没有随包分发的运行时了）
    manifest = {
        "version": version,
        "generated": datetime.datetime.now().isoformat(timespec="seconds"),
        "files": entries,
        "packages": packages,
    }
    mpath = os.path.join(upd_dir, "version.json")
    with open(mpath, "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=1)

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

    # 5. 内嵌 app.zip 到引导器并发布
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

    # 5b. 离线包：v50.11.7 起运行时不再随包分发，包里只有 exe + 两份说明
    off_zip = os.path.join(upd_dir, "PCLonline-%s-offline.zip" % version)
    readme = ("\ufeffPClonine %s\r\n\r\n"
              "【第 1 步：装运行环境（只需一次）】\r\n"
              "本启动器需要微软官方 .NET Desktop Runtime 10（64 位，约 60MB）。\r\n"
              "请自行下载安装：\r\n"
              "    https://dotnet.microsoft.com/download/dotnet/10.0\r\n"
              "打开网址 → 点 “Windows Desktop Runtime” → 选 x64 的 exe → 双击安装。\r\n"
              "（Windows 11 / 较新的 Win10 可能已自带，直接双击 exe 也能用）\r\n"
              "\r\n"
              "【第 2 步：启动】\r\n"
              "1. 把压缩包里的全部文件解压到任意文件夹\r\n"
              "2. 双击 PClonine.exe\r\n"
              "3. 缺环境时它会弹窗并给你下载链接（不会自动下载安装）\r\n"
              "\r\n"
              "【之后升级】\r\n"
              "直接用新 exe 覆盖 PClonine.exe 即可，用户数据（PCL\\ 文件夹）都会保留。\r\n"
              "在能连 GitHub 的网络下，启动器也会自动检查并安装更新。\r\n") % dict(version=version)
    with zipfile.ZipFile(off_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        z.write(out, "PClonine.exe")
        z.writestr("使用说明.txt", readme)
        z.write(env_txt, "运行环境-请先安装.txt")
    print("离线包：%s（%.1f MB）" % (off_zip, os.path.getsize(off_zip) / 1048576), flush=True)

    # 6. 上传清单（资产名必须 ASCII，否则 GitHub 会 422 / 改写成 default.txt）
    lines = [
        "发布资产（全部传到 GitHub Release 的 latest，更新只认这一个源）：",
        "",
        "  version.json      清单（ECDSA 签名）",
        "  version.json.sig  签名",
        "  app.zip            %.1f MB（无框架应用层，每次更新全量覆盖）"
        % (full_size / 1048576),
        "  PCLonline-%s-offline.zip  离线安装包（给下不动 GitHub 的玩家）" % version,
        "",
        "运行环境不再随包分发：玩家自行从微软官网下载 .NET Desktop Runtime 10（64 位），",
        "  https://dotnet.microsoft.com/download/dotnet/10.0",
        "启动器缺环境时只弹窗给链接，不联网下载、不提权安装。",
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
        "  · 应用层为无框架发布（运行时不在包里），更新时整体覆盖 app\\",
        "  · 运行环境由玩家自行从微软官网下载 .NET Desktop Runtime 10（x64），",
        "    启动器只检测并给链接，不联网下载、不提权安装",
    ]
    with open(os.path.join(upd_dir, "upload-notes.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    # 清理引导器项目里的临时 zip（避免误提交进 git）
    if os.path.exists(boot_zip):
        ctypes.windll.kernel32.DeleteFileW(boot_zip)


if __name__ == "__main__":
    main()
