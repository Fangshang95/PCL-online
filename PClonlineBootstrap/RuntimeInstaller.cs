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
    /// 确保运行时齐备（v50.11.6 定稿：CE 式——装到系统，一次 UAC，全局免疫）。
    ///
    /// Windows apphost 只认三个运行时位置：DOTNET_ROOT 环境变量（提权会丢，已废弃）、
    /// 注册表 InstallLocation（要管理员）、%ProgramFiles%\dotnet（要管理员）。
    /// 免管理员的 %LOCALAPPDATA%\dotnet 实测**不在**查找路径（v50.11.6 用窗口枚举证实：
    /// 主程序弹着缺 .NET 的 MessageBox 挂起）。所以 CE 的正解 = 把运行时装进系统：
    ///   0. 应用层自包含（v50.10.x 老安装）→ 运行时在包里，放行
    ///   1. 系统已装（%ProgramFiles%\dotnet 等）→ 直接用
    ///   2. 包内 net.zip → 申请管理员权限自我提权安装（只此一次 UAC），装完玩家再双击
    ///   3. 清单 net.zip → 联网下载后同样提权安装
    ///   4. 都没有 → 返回空串，引导器弹窗说明
    ///
    /// 返回值：null = 就绪（apphost 自行解析，无需环境变量）；"" = 缺失需提示；
    ///         "ELEVATING" = 已发起提权安装，引导器应静默退出（装完玩家再双击）。
    /// </summary>
    public static async Task<string?> EnsureAsync(string appDir, string legacyRuntimeDir,
                                                  UpdateManifest? manifest, Action<string> log)
    {
        // 0) 自包含应用层：运行时在包里
        if (IsSelfContained(appDir))
        {
            log("应用层自带运行时（自包含版），无需安装运行库");
            return null;
        }

        // 1) 系统里已装能用的运行时 → 直接放行（apphost 自己找得到，无需环境变量）
        var systemRoot = FindSystemRuntime(appDir, log);
        if (systemRoot is not null)
        {
            log("使用系统运行时：" + systemRoot);
            return null;
        }

        var baseDir = Directory.GetParent(legacyRuntimeDir)?.FullName ?? "";
        var localZip = string.IsNullOrEmpty(baseDir) ? null : FindLocalRuntimeZip(baseDir, appDir);

        // 2) 包内 net.zip（离线包形态）→ 申请提权安装到系统
        if (localZip is not null
            && (manifest?.Runtime is null || string.IsNullOrWhiteSpace(manifest.Runtime.Sha256)
                || string.Equals(UpdateService.Sha256Of(localZip), manifest.Runtime.Sha256,
                                 StringComparison.OrdinalIgnoreCase)))
        {
            return RequestElevation(localZip, baseDir, log);
        }
        if (localZip is not null)
        {
            log("包内 net.zip 与清单不符或无法解压（多半是拷贝不完整），将尝试联网下载；"
                + "建议重新获取 net.zip");
        }

        // 3) 清单 net.zip → 下载到临时目录后提权安装
        var runtime = manifest?.Runtime;
        if (runtime is not null && !string.IsNullOrWhiteSpace(runtime.Url))
        {
            log("正在下载运行时：" + runtime.Url);
            var url = UpdateService.ResolveUrl(manifest?.SourceUrl, runtime.Url);
            var tmp = Path.Combine(Path.GetTempPath(), "pclonline-net.zip");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await UpdateService.DownloadAsync(url, tmp, log, cts.Token);
                var fi = new FileInfo(tmp);
                if (fi.Exists && fi.Length == runtime.Size
                    && (string.IsNullOrWhiteSpace(runtime.Sha256)
                        || string.Equals(UpdateService.Sha256Of(tmp), runtime.Sha256,
                                         StringComparison.OrdinalIgnoreCase)))
                {
                    return RequestElevation(tmp, baseDir, log);
                }
                log("下载的运行时包校验不符，放弃");
            }
            catch (OperationCanceledException)
            {
                log("下载运行时包超时（2 分钟）");
            }
            catch (Exception ex)
            {
                log("下载运行时包失败：" + ex.Message);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        return "";
    }

    /// <summary>申请管理员权限安装 net.zip 到 %ProgramFiles%\dotnet。返回 "ELEVATING"（或失败时 ""）。</summary>
    public static string RequestElevation(string zipPath, string baseDir, Action<string> log)
    {
        log("系统缺少 .NET 运行时，正在请求管理员权限安装（只需这一次，以后永远直接启动）…");
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self)) return "";
            log("即将弹出系统授权（UAC），同意后自动安装；装完重新双击启动器即可");
            var psi = new System.Diagnostics.ProcessStartInfo(self)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = baseDir,
            };
            psi.ArgumentList.Add("--pcl-install-runtime");
            psi.ArgumentList.Add(zipPath);
            // 注意：Verb=runas 的 ShellExecuteEx 会同步阻塞到玩家点完 UAC（是/否），
            // 这句"已弹出"日志提前写，玩家看到 UAC 时引导器日志里已有说明
            System.Diagnostics.Process.Start(psi);
            log("UAC 已处理，提权安装程序已启动");
            return "ELEVATING";
        }
        catch (Exception ex)
        {
            log("申请管理员权限失败（玩家可能点了否）：" + ex.Message);
            return "";
        }
    }

    /// <summary>提权模式入口：把 net.zip 解压安装到 %ProgramFiles%\dotnet（apphost 默认查找位置）。</summary>
    public static int InstallToProgramFiles(string zipPath, Action<string> log)
    {
        try
        {
            var target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            log("安装 .NET 运行时到 " + target);
            Directory.CreateDirectory(target);
            Extract(zipPath, target, log);
            log("安装完成");
            return 0;
        }
        catch (Exception ex)
        {
            log("安装失败：" + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// 找玩家手上的 net.zip，位置宽容些：启动器同级 → 一级子文件夹（有人解压出了个子目录）
    /// → app\ 里（放错位置的也救回来）。
    /// </summary>
    public static string? FindLocalRuntimeZip(string baseDir, string appDir)
    {
        try
        {
            var candidates = new List<string> { Path.Combine(baseDir, "net.zip") };
            foreach (var d in Directory.EnumerateDirectories(baseDir))
            {
                var name = Path.GetFileName(d);
                if (name == AppDirNameMarker) continue;
                candidates.Add(Path.Combine(d, "net.zip"));
            }
            candidates.Add(Path.Combine(appDir, "net.zip"));
            foreach (var c in candidates)
            {
                if (File.Exists(c) && ZipLooksLikeRuntime(c)) return c;
            }
        }
        catch { /* 枚举失败就当没有 */ }
        return null;
    }

    private const string AppDirNameMarker = "app";



    /// <summary>
    /// "把应用层换成新版（无框架）之后还跑得起来吗？"——换 exe 升级前的保护性判断：
    /// 旧应用层可能是自包含版、直接能跑；盲目清掉再装不上运行时，玩家连旧版都没了。
    /// </summary>
    public static bool NewAppCanRun(string baseDir, string appDir)
    {
        // 自包含判断在 Program 侧（EmbeddedIsSelfContained）先行处理；
        // 这里是无框架应用层的可运行性：有 net.zip 可装、或系统已装即可
        return FindLocalRuntimeZip(baseDir, appDir) is not null      // 包内 net.zip 可装
               || FindSystemRuntime(appDir, static _ => { }) is not null;  // 系统已装
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
        // 注意：%LOCALAPPDATA%\dotnet 虽被 dotnet-install 脚本使用，但 .NET apphost
        // **不会**自动扫描它（v50.11.6 用窗口枚举实证：主程序弹缺 .NET 框挂起）。
        // 放进这里只会造成"引导器误判已装、主程序弹框"的假象，故不列入。
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
