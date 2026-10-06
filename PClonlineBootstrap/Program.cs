using System.Diagnostics;
using System.Linq;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace PClonlineBootstrap;

/// <summary>
/// PClonline 启动引导器（v50.10 热更新地基）。
///
/// 目标：保持「只转发一个 exe 给好友」的分发习惯，同时支持后续增量热更新。
/// 做法：exe 内部嵌一份压缩的应用层（app.zip）；首次运行时自展开到同目录的 app\
///       并拉起真正的主程序，此后更新只需替换 app\ 里变化的那几个文件。
/// </summary>
internal static class Program
{
    internal const string AppDirName = "app";
    internal const string RuntimeDirName = "runtime";
    internal const string AppExeName = "PClonine.exe";
    private const string MarkerName = ".bootstrapped";
    private const string ResourceName = "PClonlineBootstrap.app.zip";

    /// <summary>
    /// 更新清单唯一来源：GitHub Releases（客户端只认这一个源）。
    /// 自建更新服务器（/v1/update/manifest）已连同它的分发功能一起下线。
    /// </summary>
    private static readonly string[] DefaultManifestUrls =
    [
        "https://github.com/Fangshang95/PCL-online/releases/latest/download/version.json",
    ];

    /// <summary>
    /// 实际使用的清单源。可用环境变量 PCL_UPDATE_MANIFEST 覆盖（多个用分号分隔），
    /// 便于自建内网镜像 / 离线环境，也便于本地验证增量链路。
    /// </summary>
    private static string[] ManifestUrls()
    {
        var env = Environment.GetEnvironmentVariable("PCL_UPDATE_MANIFEST");
        if (string.IsNullOrWhiteSpace(env)) return DefaultManifestUrls;
        var list = env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Length > 0 ? list : DefaultManifestUrls;
    }

    private static string? _logPath;

    private static async Task<int> Main(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        _logPath = Path.Combine(baseDir, "bootstrap.log");
        Log("========== 引导器启动 ==========");
        Log("目录：" + baseDir);

        // v50.11.7：不再有提权安装模式。运行环境由玩家自己下载安装，
        // 启动器只负责检测 + 把官方下载链接指出来（CE 原版做法）。

        try
        {
            var appDir = Path.Combine(baseDir, AppDirName);
            var marker = Path.Combine(appDir, MarkerName);
            // 应用可以写这个标记再正常退出（设置页"退出并更新"），表示"我已退干净，
            // 请重新执行更新再拉起我"——此时文件锁全部释放，替换不会失败
            var restartMarker = Path.Combine(appDir, ".update-restart");

            while (true)
            {
                var runtimeDir = Path.Combine(baseDir, RuntimeDirName);
                // 自展开：首次运行时释放内置应用层；之后每次启动都核对"内嵌版本 vs 应用层版本"，
                // 不一致（最典型：玩家把新 exe 直接覆盖进旧目录）就重新释放——
                // 否则 app\ 里永远躺着旧版程序，界面版本号对不上、图标也是旧的，
                // 只能指望 GitHub 更新追平，而那一步在大包下不动时永远等不来
                var embeddedVer = EmbeddedVersion();
                var localVer = UpdateService.LocalVersion(appDir);
                if (!File.Exists(marker))
                {
                    Log("首次运行：释放内置运行文件到 " + AppDirName + "…");
                    ExtractApp(appDir);
                    File.WriteAllText(marker, (embeddedVer ?? "") + " " + DateTime.Now.ToString("s"), Encoding.UTF8);
                    Log("释放完成" + (string.IsNullOrEmpty(embeddedVer) ? "" : "（" + embeddedVer + "）"));
                }
                else if (!string.IsNullOrEmpty(embeddedVer)
                         && !string.Equals(embeddedVer, localVer, StringComparison.OrdinalIgnoreCase))
                {
                    // 旧应用层可能是自包含版（双击就能跑的最后一版），清掉它之后必须保证
                    // 新应用层有运行时可用，否则玩家连旧版都没了。
                    // 自包含版（运行时在包里）永远可升级；无框架版才需要找外部运行时来源
                    var embeddedSelfContained = EmbeddedIsSelfContained();
                    if (embeddedSelfContained || RuntimeInstaller.NewAppCanRun(baseDir, appDir))
                    {
                        Log("内置版本 " + embeddedVer + " 与应用层 " + (localVer ?? "（未知）") +
                            " 不同：清理旧应用层后重新释放…");
                        // 旧应用层可能是自包含版（带整套运行时 dll），只覆盖不清理会新旧混装，
                        // 版本错位的 dll 有隐患；用户数据在根目录 PCL\，删 app\ 不伤数据
                        try { Directory.Delete(appDir, true); }
                        catch (Exception ex) { Log("清理旧应用层失败（可能有文件被占用）：" + ex.Message); }
                    ExtractApp(appDir);
                    File.WriteAllText(marker, embeddedVer + " " + DateTime.Now.ToString("s"), Encoding.UTF8);
                    Log("释放完成（" + embeddedVer + "）");
                    // 防呆：这个 exe 不能直接双击（无框架版需要引导器带 runtime\）。
                    // 实测有玩家会点进 app\ 双击它，结果弹系统的"缺 .NET Desktop Runtime"
                    try
                    {
                        File.WriteAllText(Path.Combine(appDir, "！！别双击我——请运行上级目录的 PClonine.exe.txt"),
                            "PClonine.exe（主程序）不能直接双击：它需要启动器装好的 .NET 运行时。\r\n"
                            + "请回到上一级文件夹，双击 PClonline.exe 启动。\r\n");
                    }
                    catch { }
                    }
                    else
                    {
                        Log("内置版本 " + embeddedVer + " 与应用层 " + (localVer ?? "（未知）") +
                            " 不同，但系统里没装可用的 .NET——保留旧版继续运行；"
                            + "请先按提示安装 .NET 运行环境（" + RuntimeInstaller.DownloadUrl +
                            "），之后重新双击即完成升级");
                    }
                }
                else
                {
                    Log("已展开，跳过释放（应用层 " + (localVer ?? "未知") + "）");
                }

                // 全量更新（只换 app\，运行时目录不参与；失败一律不阻断启动）
                var urls = ManifestUrls();
                Log("更新清单源：" + string.Join(" | ", urls));
                var manifest = await UpdateService.RunAsync(appDir, urls, Log);

                var exe = Path.Combine(appDir, AppExeName);
                if (!File.Exists(exe))
                {
                    Log("错误：找不到应用主程序 " + exe);
                    return 2;
                }

                // 应用层是无框架版：需要玩家系统里已有 .NET 运行时。
                // apphost 会自己扫标准安装位置，所以**不需要也不应该**传 DOTNET_ROOT——
                // UAC 提权的新进程不继承环境变量，传了反而埋雷（v50.11.4 主程序
                // 0xC0000417 弹英文缺 .NET 就是这么来的）。
                // runtimeDir 实参仅用于识别并迁移老的 runtime\ 外挂安装。
                var runtimeRoot = await RuntimeInstaller.EnsureAsync(appDir, runtimeDir, manifest, Log);
                if (runtimeRoot == "")
                {
                    // 没有运行时还硬拉起无框架程序 = 玩家看到的就是"闪一下就没了"。
                    // 这里停下来把话说清楚，并给出官方下载链接，由玩家自行安装。
                    MessageBoxW(IntPtr.Zero,
                        "启动器需要 .NET 运行环境，你的电脑上还没装。\n\n" +
                        "请自行下载安装（官方微软网站，约 60MB）：\n" +
                        RuntimeInstaller.DownloadUrl + "\n\n" +
                        "安装时选「Windows Desktop Runtime」即可。\n" +
                        "装好后重新双击 PClonine.exe 就能启动。",
                        "PClonline 需要安装运行环境", 0x40);
                    return 4;
                }

                Log("拉起应用：" + exe);
                var psi = new ProcessStartInfo(exe) { WorkingDirectory = baseDir };
                // 应用层搬到了 app\ 子目录，但用户数据（实例/版本/设置/日志/服务器档案）
                // 必须留在启动器根目录——否则老用户一升级就"什么都没了"。
                // PCL_DATA_DIR：PCL.Core.Basics.ExecutableDirectory 会优先采用它；
                // 工作目录设为 baseDir，让 mcstudio 配置的旧位置兜底也能命中。
                psi.Environment["PCL_DATA_DIR"] = baseDir;
                Log("数据目录：" + baseDir + "（工作目录同此）");
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi);
                if (proc is null) { Log("错误：启动进程失败"); return 3; }
                // 必须等待：引导器一旦退出，拉起的应用可能被连带终止（实测在受限环境中
                // 引导器退出后主程序立刻消失、连日志目录都来不及建）。同时用于实现
                // "应用退出后继续完成更新"。
                Log("应用已启动，PID " + proc.Id + "，等待其退出…");
                proc.WaitForExit();
                Log("应用已退出，退出码 " + proc.ExitCode);

                if (!File.Exists(restartMarker)) return proc.ExitCode;
                File.Delete(restartMarker);
                Log("应用请求重启以完成更新，重新执行更新检查…");
            }
        }
        catch (Exception ex)
        {
            Log("启动失败：" + ex);
            return 1;
        }
    }

    /// <summary>内嵌 app.zip 的 runtimeconfig 是否用 includedFrameworks（自包含，运行时在包里）。</summary>
    private static bool EmbeddedIsSelfContained()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(ResourceName);
            if (stream is null) return false;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return false;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd().Contains("includedFrameworks");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读内嵌 app.zip 里 version.json 的版本号（exe 即版本：换 exe 就等于换应用层）。</summary>
    private static string EmbeddedVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(ResourceName);
            if (stream is null) return "";
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry("version.json");
            if (entry is null) return "";
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            using var doc = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            return doc.RootElement.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>从内嵌资源释放应用层到目标目录（目录已存在时先清空，避免残留旧文件混淆）。</summary>
    private static void ExtractApp(string appDir)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("内置运行文件缺失（app.zip 未嵌入）");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        // 注意：不做清空目录——否则中断后重跑会从头再来
        Directory.CreateDirectory(appDir);
        var done = 0;
        var skipped = 0;
        foreach (var entry in zip.Entries)
        {
            var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.Combine(appDir, rel);
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(full);
                continue;
            }
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 断点续释放：已存在且大小一致就跳过——被中断/被杀/关机后重跑接着放
            var fi = new FileInfo(full);
            if (fi.Exists && fi.Length == entry.Length) { skipped++; continue; }
            entry.ExtractToFile(full, overwrite: true);
            done++;
            if (done % 100 == 0) Log($"已释放 {done} 个文件…");
        }
        Log($"释放完成：新增 {done} 个，跳过（已完整）{skipped} 个");
    }

    /// <summary>原生弹窗：避免为了一个提示把 WinForms/WPF 拖进引导器。</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    private static void Log(string message)
    {
        if (_logPath is null) return;
        try
        {
            File.AppendAllText(_logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}
