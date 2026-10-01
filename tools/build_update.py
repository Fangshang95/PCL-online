#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
v50.11 热更新打包脚本（两层分离：运行时 / 应用层）

结构（v50.11 起，取代 v50.10 的「全量自包含 + 文件级增量」方案）：
  运行时 runtime\  —— 只装一次，之后永不动
  应用层 app\      —— 每次更新全量覆盖

产出（放到同一个 GitHub Release 的 latest 下）：
  version.json     清单（ECDSA 签名）
  version.json.sig 签名
  app.zip          无框架应用层（framework-dependent，不含运行时）
  net.zip          .NET 运行时包（hostfxr + shared\<framework>\<ver>\*）

玩家侧流程：
  1. 首跑：exe 内嵌 app.zip → 自展开到 app\（只有应用层，几十个文件）
  2. 缺运行时：引导器先探测系统里有没有够用的 .NET 10；没有就下 net.zip
     解压到 runtime\（标准安装布局），之后每次更新都不会再碰它
  3. 更新：只下 app.zip 全量，覆盖 app\ 里的文件，运行时目录完全不动
  → 于是「基线版本 / diff / remove 列表 / 多份 patch」这套复杂度全部消失

为什么 net.zip 直接取自本机 SDK：
  sc（self-contained）publish 出来的是「apphost + 运行时 dll 平铺」，
  不能直接当 DOTNET_ROOT 用（hostfxr 要找 shared\<framework>\<ver>\ 布局），
  所以这里按 fx 应用声明的框架版本，从 SDK 根目录挑出
  {dotnet.exe, hostfxr.dll, hostpolicy.dll, shared\<name>\<ver>\*\*} 打成包。

用法：
  python build_update.py v50.11.0
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


def pick_runtime_files(fw_list):
    """在本机 SDK 里为每个 framework 挑一个可用版本，返回 {abs_root: [rel]}

    版本选择沿用 .NET 默认 roll-forward：≥ 声明版本的最高版本（这里取第一个满足的）。
    根文件固定带 dotnet.exe / hostfxr.dll / hostpolicy.dll —— 移动运行时最小集合。
    """
    roots = {}

    def add_tree(root, sub):
        base = os.path.join(root, sub)
        if not os.path.isdir(base):
            return 0
        n = 0
        for dirpath, _, files in os.walk(base):
            for f in files:
                rel = os.path.relpath(os.path.join(dirpath, f), root).replace("\\", "/")
                roots.setdefault(root, []).append(rel)
                n += 1
        return n

    tops = {}
    for name, want in fw_list:
        shared = os.path.join(SDK_DIR, "shared", name)
        if not os.path.isdir(shared):
            sys.exit("SDK 里没有运行时 %s（%s 缺失）" % (name, shared))
        vers = []
        for v in sorted(os.listdir(shared)):
            p = os.path.join(shared, v)
            if os.path.isdir(p):
                vers.append((tuple(int(x) for x in v.split(".") if x.isdigit()), v))
        if not vers:
            sys.exit("SDK 里 %s 没有可用版本" % name)
        want_t = tuple(int(x) for x in want.split(".") if x.isdigit())
        pick = None
        for t, v in vers:
            if t >= want_t:
                pick = v
                break
        if pick is None:
            pick = vers[-1][1]
            print("  警告：%s 声明 %s，SDK 最高只到 %s，将用后者" % (name, want, pick))
        n = add_tree(SDK_DIR, "shared/%s/%s" % (name, pick))
        tops[name] = pick
        print("  运行时 %s → shared/%s/%s（%d 个文件）" % (name, name, pick, n))

    # 移动运行时的根文件：dotnet.exe + 与 shared 版本对齐的 host\fxr\<ver>\
    if not os.path.isfile(os.path.join(SDK_DIR, "dotnet.exe")):
        sys.exit("SDK 缺少 dotnet.exe")
    roots.setdefault(SDK_DIR, []).append("dotnet.exe")
    host_ver = max(tops.values()) if tops else ""
    host_dir = os.path.join(SDK_DIR, "host", "fxr", host_ver)
    if not os.path.isdir(host_dir):
        sys.exit("SDK 缺少 host\\fxr\\%s（hostfxr 版本与运行时不一致）" % host_ver)
    n = add_tree(SDK_DIR, "host/fxr/%s" % host_ver)
    print("  hostfxr → host/fxr/%s（%d 个文件）" % (host_ver, n))
    return roots


def main():
    version = sys.argv[1] if len(sys.argv) > 1 else "v50.11.0"
    today = datetime.date.today().strftime("%Y%m%d")
    print("=== 打包 %s ===" % version, flush=True)

    # 1. 发布应用层（无框架：运行时不随包走，交给 runtime\ 或玩家系统）
    print("--- 发布应用层（无框架）---", flush=True)
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
    # 2. 组装运行时包（net.zip）
    print("--- 组装运行时 ---", flush=True)
    fw_list = read_frameworks(APP_DIR)
    if not fw_list:
        sys.exit("应用层 runtimeconfig 里没声明 framework，无法组装运行时包")
    print("  声明的框架：%s" % ", ".join("%s %s" % (n, v) for n, v in fw_list), flush=True)
    rt_roots = pick_runtime_files(fw_list)
    rt_rels = sorted({r for rels in rt_roots.values() for r in rels})
    rt_entries = []
    for root, rels in rt_roots.items():
        for rel in rels:
            full = os.path.join(root, rel.replace("/", os.sep))
            rt_entries.append({"path": rel, "size": os.path.getsize(full),
                               "sha256": sha256_of(full)})
    rt_entries.sort(key=lambda e: e["path"])
    print("运行时：%d 个文件，共 %.1f MB" % (len(rt_entries),
                                       sum(e["size"] for e in rt_entries) / 1048576), flush=True)

    out_dir = os.path.join(DIST, version)
    upd_dir = os.path.join(out_dir, "update")
    os.makedirs(upd_dir, exist_ok=True)

    # 3. 两个包
    all_rels = [e["path"] for e in entries]
    # 3a. 内嵌进 exe 的那份（带清单，自展开后 app\ 里就有版本标记）
    embed_zip = os.path.join(BUILD, "app.zip")
    zip_files(embed_zip, {APP_DIR: all_rels + ["version.json"]})
    # 3b. 对外发布的全量应用层
    full_zip = os.path.join(upd_dir, "app.zip")
    full_size = zip_files(full_zip, {APP_DIR: all_rels})
    # 3c. 运行时包
    net_zip = os.path.join(upd_dir, "net.zip")
    net_size = zip_files(net_zip, rt_roots)
    print("app.zip：%.1f MB（无框架应用层）" % (full_size / 1048576), flush=True)
    print("net.zip ：%.1f MB（.NET 运行时）" % (net_size / 1048576), flush=True)

    # 包地址一律写相对文件名：客户端按"清单取自哪个地址"拼绝对 URL，
    # 清单字节原样返回才不会破坏 ECDSA 签名
    packages = [{
        "type": "full", "from": "",
        "url": "app.zip",
        "size": full_size, "sha256": sha256_of(full_zip),
    }]
    runtime_block = {
        "url": "net.zip",
        "size": net_size,
        "sha256": sha256_of(net_zip),
        "count": len(rt_entries),
        "files": rt_entries[:200],   # 完整性抽样校验用，全量清单反而没必要下
    }

    # 4. version.json + 签名（只写到发布目录；APP_DIR\version.json 是 1b 写的简版标记，
    #    千万别用完整清单盖回去——下次构建又会把它当残留嵌进 exe）
    manifest = {
        "version": version,
        "generated": datetime.datetime.now().isoformat(timespec="seconds"),
        "files": entries,
        "runtime": runtime_block,
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

    # 5b. 离线包：exe + net.zip（给连不上 GitHub / 下不动大包的玩家，解压即装、全程不联网）
    off_zip = os.path.join(upd_dir, "PCLonline-%s-offline.zip" % version)
    readme = ("\ufeffPClonine 离线安装包 %s\r\n\r\n"
              "1. 把压缩包里的全部文件解压到任意文件夹（放哪都行，别放在带 # 的路径里）\r\n"
              "2. 双击 PClonline.exe，启动器会自动释放程序并从旁边的 net.zip 安装运行时\r\n"
              "   —— 全程不需要联网\r\n"
              "3. 之后想升级：直接用新 exe 覆盖 PClonline.exe 即可，程序本体和用户数据都会保留\r\n"
              "   （在能连 GitHub 的网络下，启动器也会自动检查并安装更新）\r\n") % dict(version=version)
    with zipfile.ZipFile(off_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        z.write(out, "PClonline.exe")
        # net.zip 本身就是压缩包，再 DEFLATE 一遍几乎不省体积（实测省 0.6%）却让解压慢好几倍，
        # 直接原样存进去——玩家用任何解压工具都能秒开
        z.write(net_zip, "net.zip", zipfile.ZIP_STORED)
        z.writestr("使用说明.txt", readme)
    print("离线包：%s（%.1f MB）" % (off_zip, os.path.getsize(off_zip) / 1048576), flush=True)

    # 6. 上传清单（资产名必须 ASCII，否则 GitHub 会 422 / 改写成 default.txt）
    lines = [
        "发布资产（全部传到 GitHub Release 的 latest，更新只认这一个源）：",
        "",
        "  version.json      清单（ECDSA 签名）",
        "  version.json.sig  签名",
        "  app.zip            %.1f MB（无框架应用层，每次更新全量覆盖）" % (full_size / 1048576),
        "  net.zip            %.1f MB（.NET 运行时，装一次就再也不用下）" % (net_size / 1048576),
        "  PCLonline-%s-offline.zip  离线安装包（exe + net.zip，给下不动 GitHub 的玩家）" % version,
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
        "  · app.zip 无框架（不含运行时），运行时由 net.zip 装到 runtime\\ 目录，",
        "    更新时只覆盖 app\\，runtime\\ 永远不动",
    ]
    with open(os.path.join(upd_dir, "upload-notes.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    # 清理引导器项目里的临时 zip（避免误提交进 git）
    if os.path.exists(boot_zip):
        ctypes.windll.kernel32.DeleteFileW(boot_zip)


if __name__ == "__main__":
    main()
