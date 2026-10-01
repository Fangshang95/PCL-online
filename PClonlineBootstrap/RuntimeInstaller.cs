using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace PClonlineBootstrap;

/// <summary>
/// 应用层运行时的自动就位（v50.10.3）。
///
/// 分发策略：
///   首次下载的全量包 = 自包含引导器（自带运行时，玩家双击就能跑）+ 无框架版应用层
///   → 引导器在拉起应用层前检查系统里有没有对应版本，没有就自动下载装到 %LOCALAPPDATA%\dotnet；
///   之后每次更新（全量兜底 / 增量）只有 PCL 自己的文件，运行时不再随包下发。
///
/// 判据是应用层的 runtimeconfig.json（无框架版必然带它）：老的自包含版本没有这个文件，
/// 因此不会在玩家机器上白下载一份运行时。
///
/// 安装方式是「官方运行时 zip 直下 + 解压」，不用 dotnet-install.ps1：
/// 实测部分机器 PowerShell 执行策略受限、或安装脚本在下载阶段挂死，解压这条路径不依赖脚本解释器。
/// </summary>
internal static class RuntimeInstaller
{
    /// <summary>微软官方运行时分发根。</summary>
    private const string BaseUrl = "https://builds.dotnet.microsoft.com/dotnet";
    private const string DotnetInstallRootName = "dotnet";

    /// <summary>本机运行时安装位置（用户级，不需要管理员权限）。</summary>
    private static readonly string InstallRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DotnetInstallRootName);

    /// <summary>运行时搜索路径：环境变量优先，其次是系统/用户级 dotnet 安装目录。</summary>
    private static readonly string[] SearchRoots = BuildSearchRoots();

    private static string[] BuildSearchRoots()
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (!string.IsNullOrEmpty(p) && Directory.Exists(Path.Combine(p, "shared")))
                list.Add(p!.TrimEnd(Path.DirectorySeparatorChar));
        }

        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), DotnetInstallRootName));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), DotnetInstallRootName));
        Add(InstallRoot);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>应用层声明的运行时（例如 dotnet 10.0.0 + WindowsDesktop 10.0.0）。</summary>
    private sealed record Requirement(string Name, string Version);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>
    /// 确保应用层需要的运行时齐备。返回运行时所在目录（用于拉起时设 DOTNET_ROOT）；
    /// 返回 null 表示无需特殊处理（已装在系统里或应用自带）；缺了且装不上时返回空串。
    /// </summary>
    public static async Task<string?> EnsureAsync(string appDir, Action<string> log)
    {
        var reqs = ReadRequirements(appDir, log);
        var missing = reqs.Where(r => !HasRuntime(r)).ToList();
        if (missing.Count == 0)
        {
            foreach (var r in reqs) log("运行时已就位：" + r.Name + " " + r.Version);
            return null;
        }

        foreach (var need in missing)
        {
            log("正在自动安装运行时：" + need.Name + " " + need.Version);
            if (!await InstallAsync(need, log)) return "";
        }
        return reqs.All(HasRuntime) ? InstallRoot : "";
    }

    /// <summary>读应用层的 runtimeconfig.json；自包含版本没有这个文件，返回空。</summary>
    private static Requirement[] ReadRequirements(string appDir, Action<string> log)
    {
        // 文件名是「程序集 + .runtimeconfig.json」（没有 .exe），通配匹配免得跟着改名走
        foreach (var path in Directory.EnumerateFiles(appDir, "*.runtimeconfig.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                // 框架声明在 runtimeOptions.frameworks 下（frameworks 也可能被折叠到顶层，两边都看一眼）
                var ro = doc.RootElement.TryGetProperty("runtimeOptions", out var r) ? r : doc.RootElement;
                if (!ro.TryGetProperty("frameworks", out var fs) || fs.ValueKind != JsonValueKind.Array)
                {
                    log("应用层是自带运行时版，不需要系统运行时：" + Path.GetFileName(path));
                    return Array.Empty<Requirement>();
                }
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

    private static bool HasRuntime(Requirement need)
    {
        var (maj, min) = Split(need.Version);
        foreach (var root in SearchRoots)
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

    /// <summary>framework 名字 → 官方目录名 / 文件名前缀。</summary>
    private static bool TryGetKind(string name, out string dir, out string prefix)
    {
        dir = prefix = "";
        if (name == "Microsoft.NETCore.App") { dir = "Runtime"; prefix = "dotnet"; return true; }
        if (name == "Microsoft.WindowsDesktop.App") { dir = "WindowsDesktop"; prefix = "windowsdesktop"; return true; }
        if (name == "Microsoft.AspNetCore.App") { dir = "AspNetCore"; prefix = "aspnetcore"; return true; }
        return false;
    }

    private static async Task<bool> InstallAsync(Requirement need, Action<string> log)
    {
        if (!TryGetKind(need.Name, out var dir, out var prefix))
        {
            log("未知道具运行时：" + need.Name);
            return false;
        }
        var ver = await LatestVersionAsync(dir, need.Version, log) ?? need.Version;
        var arch = Environment.Is64BitProcess ? "x64" : "arm64";
        var url = $"{BaseUrl}/{dir}/{ver}/{prefix}-runtime-{ver}-win-{arch}.zip";
        var zip = Path.Combine(Path.GetTempPath(), $"{prefix}-runtime-{ver}-win-{arch}.zip");
        try
        {
            log("下载运行时：" + url);
            await DownloadAsync(url, zip, log);
            Extract(zip, InstallRoot, log);
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
        var (maj, min) = Split(ver);
        var shared = Path.Combine(InstallRoot, "shared", need.Name);
        bool ok = Directory.Exists(shared)
                  && Directory.EnumerateDirectories(shared).Any(d => { var (m, n) = Split(Path.GetFileName(d)); return m == maj && n == min; });
        if (!ok) { log("运行时解压后未找到 " + need.Name + " " + ver); return false; }
        log("运行时安装完成：" + need.Name + " " + ver + " → " + InstallRoot);
        return true;
    }

    /// <summary>取该 major.minor 通道的最新补丁版（装不上就退回清单里的版本）。</summary>
    private static async Task<string?> LatestVersionAsync(string dir, string required, Action<string> log)
    {
        var (maj, min) = Split(required);
        try
        {
            var text = await Http.GetStringAsync($"{BaseUrl}/{dir}/{maj}.{min}/latest.version");
            var v = text.Trim();
            if (v.Length > 0 && char.IsDigit(v[0])) return v;
        }
        catch (Exception ex)
        {
            log("获取版本号失败（" + dir + "），退回 " + required + "：" + ex.Message);
        }
        return null;
    }

    private static async Task DownloadAsync(string url, string dest, Action<string> log)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var src = await resp.Content.ReadAsStreamAsync();
        var buffer = new byte[1 << 16];
        long read = 0, last = 0;
        int n;
        while ((n = await src.ReadAsync(buffer)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (read - last >= 8 << 20)
            {
                last = read;
                log(total > 0 ? $"下载运行时 {read / 1048576}/{total / 1048576} MB"
                              : $"下载运行时 {read / 1048576} MB");
            }
        }
        log("运行时下载完成 " + (read / 1048576) + " MB");
    }

    /// <summary>解压到目标目录：已存在且大小一致就跳过（断点续传式，装一半也不会重来）。</summary>
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
