using System.IO.Compression;
using System.Text.Json;
using System.Threading;

namespace PClonlineBootstrap;

/// <summary>
/// 运行时就位（v50.11，两层分离方案）。
///
/// 分发结构：runtime\ 只装一次（之后永不动），app\ 每次更新全量覆盖。
/// 因此运行时有两条来源，按序尝试：
///   1. 本地 runtime\ —— 清单里 runtime 字段抽样校验通过就用，更新不会碰它；
///   2. 清单里的 net.zip —— 缺了就下一个移动版运行时解压到 runtime\
///      （hostfxr.dll + shared\&lt;framework&gt;\&lt;ver&gt;\*，标准安装布局，可直接当 DOTNET_ROOT）；
///   3. 都不行就用玩家系统里已有的 .NET（省一次大下载）。
///
/// 安装用 zip 直下 + 解压，不用 dotnet-install.ps1：实测部分机器 PowerShell 执行策略受限、
/// 或安装脚本在下载阶段挂死，解压这条路径不依赖脚本解释器。
/// </summary>
internal static class RuntimeInstaller
{
    /// <summary>应用层声明的运行时（例如 dotnet 10.0.0 + WindowsDesktop 10.0.0）。</summary>
    private sealed record Requirement(string Name, string Version);

    /// <summary>抽样校验用的文件数：过多就只查前几个，别为了几个字节去读几十 MB。</summary>
    private const int ProbeCount = 5;

    /// <summary>
    /// 确保运行时齐备。返回应当传给应用的 DOTNET_ROOT（本地 runtime\ 或系统安装目录）；
    /// 返回 null 表示系统里已有（无需指定）；缺了且装不上时返回空串（由调用方提示玩家）。
    /// </summary>
    public static async Task<string?> EnsureAsync(string appDir, string runtimeDir,
                                                  UpdateManifest? manifest, Action<string> log)
    {
        var runtime = manifest?.Runtime;
        // 清单来源：net.zip 在清单里写的是相对文件名，要按"清单取自哪个地址"拼绝对 URL
        var sourceUrl = manifest?.SourceUrl;
        if (string.IsNullOrWhiteSpace(sourceUrl)) sourceUrl = null;

        // 0) 应用层是自包含版（v50.10.x 的老安装，runtimeconfig 用 includedFrameworks 声明）：
        //    运行时已经打在包里，既不需要 runtime\ 也不该弹"缺运行库"的框——
        //    老用户换新引导器后 app\ 还是旧版时，就是这种情况
        if (IsSelfContained(appDir))
        {
            log("应用层自带运行时（自包含版），无需安装运行库");
            return null;
        }

        // 1) 本地 runtime\ 还在且完整 —— 更新永远走不到这里，这是常态
        if (IsComplete(runtimeDir, runtime, log))
        {
            log("运行时已就位：" + runtimeDir);
            return runtimeDir;
        }

        var baseDir = Directory.GetParent(runtimeDir)?.FullName;
        var localZip = string.IsNullOrEmpty(baseDir) ? null : Path.Combine(baseDir, "net.zip");
        var stamp = Path.Combine(runtimeDir, ".installed");

        // 1b) 离线快速通道：清单拉不到时 IsComplete 没有校验基准，
        //     靠安装时写的 .installed 指纹（net.zip 大小+修改时间）判定"装过且包没变"，
        //     否则离线玩家每次启动都要重解压一遍 171MB
        if (runtime is null && localZip is not null && File.Exists(localZip)
            && File.Exists(stamp))
        {
            try
            {
                if (File.ReadAllText(stamp).Trim() == ZipFingerprint(localZip))
                {
                    log("运行时已就位（离线）：" + runtimeDir);
                    return runtimeDir;
                }
            }
            catch { /* 标记坏了就当没装过 */ }
        }

        // 2) 离线包：启动器同级目录的 net.zip（发布时的离线压缩包就是 exe + net.zip）。
        //    必须放在"读清单"之前——离线场景清单本来就拉不到，不能因为它就装不了运行时
        if (localZip is not null && File.Exists(localZip) && ZipLooksLikeRuntime(localZip)
            && (runtime is null || string.IsNullOrWhiteSpace(runtime.Sha256)
                || string.Equals(UpdateService.Sha256Of(localZip), runtime.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            log("使用离线运行时包：" + localZip);
            try
            {
                Extract(localZip, runtimeDir, log);
                try { File.WriteAllText(stamp, ZipFingerprint(localZip)); } catch { }
                log("运行时安装完成：" + runtimeDir);
                return runtimeDir;
            }
            catch (Exception ex)
            {
                log("离线运行时包解压失败：" + ex.Message + "，改走下载");
            }
        }

        // 3) 从清单给的 net.zip 装一份移动运行时
        if (runtime is not null && !string.IsNullOrWhiteSpace(runtime.Url))
        {
            log("正在安装运行时：" + runtime.Url);
            var url = UpdateService.ResolveUrl(sourceUrl, runtime.Url);
            if (await TryInstallPackageAsync(url, runtimeDir, runtime, log)) return runtimeDir;
        }

        // 4) 兜底：玩家机器上已经装了能用的运行时，就别再拖一份几十 MB 下来
        var systemRoot = FindSystemRuntime(appDir, log);
        if (systemRoot is not null)
        {
            log("使用系统运行时：" + systemRoot);
            return systemRoot;
        }
        return "";
    }

    /// <summary>离线安装标记：net.zip 的大小 + 修改时间指纹。包没换过 = runtime\ 不用重装。</summary>
    private static string ZipFingerprint(string zipPath)
    {
        var fi = new FileInfo(zipPath);
        return fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
    }

    /// <summary>粗检一个 zip 是不是像样的运行时包：能打开、里面有 dotnet.exe。</summary>
    private static bool ZipLooksLikeRuntime(string zipPath)
    {
        try
        {
            using var z = ZipFile.OpenRead(zipPath);
            foreach (var e in z.Entries)
            {
                if (e.FullName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { /* 打不开就当不是 */ }
        return false;
    }

    /// <summary>应用层 runtimeconfig 用 includedFrameworks 声明 = 自包含发布（运行时在包里）。</summary>
    private static bool IsSelfContained(string appDir)
    {
        foreach (var path in Directory.EnumerateFiles(appDir, "*.runtimeconfig.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var ro = doc.RootElement.TryGetProperty("runtimeOptions", out var r) ? r : doc.RootElement;
                if (ro.TryGetProperty("includedFrameworks", out _)) return true;
            }
            catch { /* 坏文件按需安装处理 */ }
        }
        return false;
    }

    /// <summary>抽样校验 runtime\ 是否完整（文件数 + 前几个文件的 sha256）。</summary>
    private static bool IsComplete(string dir, ManifestRuntime? spec, Action<string> log)
    {
        if (spec is null || spec.Count <= 0) return false;
        if (!Directory.Exists(dir)) return false;
        var probe = spec.Files.Take(ProbeCount).ToList();
        if (probe.Count == 0) return false;

        var missing = probe.Count(p => !File.Exists(Path.Combine(dir, p.Path.Replace('/', Path.DirectorySeparatorChar))));
        if (missing > 0)
        {
            log("runtime\\ 缺 " + missing + " 个样本文件，判定不完整");
            return false;
        }
        foreach (var f in probe)
        {
            var full = Path.Combine(dir, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!string.Equals(UpdateService.Sha256Of(full), f.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                log("runtime\\ 内容已变（" + f.Path + "），重新装运行时");
                return false;
            }
        }
        var onDisk = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
        if (onDisk < spec.Count * 0.9)
        {
            log($"runtime\\ 只有 {onDisk}/{spec.Count} 个文件，判定不完整");
            return false;
        }
        return true;
    }

    /// <summary>
    /// 从清单地址下 net.zip 并解压到 runtime\；解压后按清单复核关键文件。
    ///
    /// 每步都必须有日志：这一路是「首次启动 + 走代理/弱网」最容易出事的地方，
    /// 卡住时玩家只看得见静默转圈，所以下载开始/结束/大小/校验/解压每一步都要留痕。
    /// 另有 10 分钟硬超时——代理或网络假装连通却不给数据（502 后挂死）时，
    /// 宁可失败去走系统运行时兜底，也别把玩家吊住一小时。
    /// </summary>
    private static async Task<bool> TryInstallPackageAsync(string url, string dir, ManifestRuntime spec,
                                                           Action<string> log)
    {
        var zip = Path.Combine(Path.GetTempPath(), "pclonline-net.zip");
        try
        {
            if (File.Exists(zip) && spec.Size > 0 && new FileInfo(zip).Length == spec.Size)
            {
                // 上次下完就中断了：大小正好，直接接着解压，别再拖一遍
                log("复用已下载的运行时包（" + (spec.Size / 1048576) + " MB）");
            }
            else
            {
                log("下载运行时包：" + url);
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                try
                {
                    await UpdateService.DownloadAsync(url, zip, log, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    log("下载运行时包超时（10 分钟），跳过联网安装");
                    return false;
                }
                var got = new FileInfo(zip);
                if (!got.Exists || got.Length != spec.Size)
                {
                    log($"运行时包大小不符（实际 {(got.Exists ? got.Length : 0)} 字节，期望 {spec.Size}）");
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(spec.Sha256)
                    && !string.Equals(UpdateService.Sha256Of(zip), spec.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    log("运行时包校验失败（sha256 不符），丢弃");
                    return false;
                }
                log("运行时包校验通过：" + (spec.Size / 1048576) + " MB");
            }

            log("解压运行时到 runtime\\…");
            Extract(zip, dir, log);
        }
        catch (Exception ex)
        {
            log("运行时安装失败：" + ex.Message);
            return false;
        }
        finally
        {
            try { File.Delete(zip); } catch { /* 临时文件删不掉无所谓 */ }
        }
        if (!IsComplete(dir, spec, log)) { log("运行时解压后校验不通过"); return false; }
        log("运行时安装完成：" + dir);
        return true;
    }

    /// <summary>系统 / 用户级 dotnet 里挑一个满足应用层声明的目录；挑不到返回 null。</summary>
    private static string? FindSystemRuntime(string preferDir, Action<string> log)
    {
        var reqs = ReadRequirements(preferDir, log);
        if (reqs.Length == 0) return null;
        foreach (var root in SearchRoots())
        {
            if (reqs.All(HasRuntime)) return root;
        }
        return null;
    }

    private static Requirement[] ReadRequirements(string appDir, Action<string> log)
    {
        foreach (var path in Directory.EnumerateFiles(appDir, "*.runtimeconfig.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var ro = doc.RootElement.TryGetProperty("runtimeOptions", out var r) ? r : doc.RootElement;
                if (!ro.TryGetProperty("frameworks", out var fs) || fs.ValueKind != JsonValueKind.Array) continue;
                var list = new List<Requirement>();
                foreach (var e in fs.EnumerateArray())
                {
                    var n = e.TryGetProperty("name", out var x) ? x.GetString() : null;
                    var v = e.TryGetProperty("version", out var y) ? y.GetString() : null;
                    if (!string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(v)) list.Add(new Requirement(n!, v!));
                }
                return list.ToArray();
            }
            catch (Exception ex)
            {
                log("解析 " + Path.GetFileName(path) + " 失败：" + ex.Message);
            }
        }
        return Array.Empty<Requirement>();
    }

    private static string[] SearchRoots()
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (!string.IsNullOrEmpty(p) && Directory.Exists(Path.Combine(p, "shared")))
                list.Add(p!.TrimEnd(Path.DirectorySeparatorChar));
        }

        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dotnet"));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool HasRuntime(Requirement need)
    {
        var (maj, min) = Split(need.Version);
        foreach (var root in SearchRoots())
        {
            var dir = Path.Combine(root, "shared", need.Name);
            if (!Directory.Exists(dir)) continue;
            foreach (var v in Directory.EnumerateDirectories(dir))
            {
                var (m2, n2) = Split(Path.GetFileName(v));
                if (m2 == maj && n2 == min) return true;
            }
        }
        return false;
    }

    /// <summary>解压到根目录：已存在且大小一致就跳过（断点续式，装一半重来也不会从头）。</summary>
    private static void Extract(string zip, string root, Action<string> log)
    {
        Directory.CreateDirectory(root);
        using var z = ZipFile.OpenRead(zip);
        var done = 0;
        foreach (var e in z.Entries)
        {
            if (e.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
            var dest = Path.Combine(root, e.FullName.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var fi = new FileInfo(dest);
            if (fi.Exists && fi.Length == e.Length) { done++; continue; }
            var tmp = dest + ".part";
            try { e.ExtractToFile(tmp, true); }
            finally
            {
                try { File.Move(tmp, dest, true); } catch { /* 目标被占用时保留原文件 */ }
            }
            done++;
        }
        log("运行时解压完成，写入 " + done + " 个文件");
    }

    private static (int Major, int Minor) Split(string? version)
    {
        var parts = (version ?? "0.0").Split('.');
        int.TryParse(parts.Length > 0 ? parts[0] : "0", out var maj);
        int.TryParse(parts.Length > 1 ? parts[1] : "0", out var min);
        return (maj, min);
    }
}
