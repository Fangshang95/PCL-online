using System.Diagnostics;
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
    private const string AppDirName = "app";
    private const string AppExeName = "Plain Craft Launcher 2.exe";
    private const string MarkerName = ".bootstrapped";
    private const string ResourceName = "PClonlineBootstrap.app.zip";

    /// <summary>默认更新清单源，按顺序尝试（第一个可用的为准）。
    /// 服务器那个接口由服务端自行判断"在线人数少且带宽空闲"才返回清单，否则跳过。</summary>
    private static readonly string[] DefaultManifestUrls =
    [
        "https://github.com/Fangshang95/PCL-online/releases/latest/download/version.json",
        "http://120.26.198.92:8801/v1/update/manifest",
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

        try
        {
            var appDir = Path.Combine(baseDir, AppDirName);
            var marker = Path.Combine(appDir, MarkerName);
            // 应用可以写这个标记再正常退出（设置页"退出并更新"），表示"我已退干净，
            // 请重新执行更新再拉起我"——此时文件锁全部释放，替换不会失败
            var restartMarker = Path.Combine(appDir, ".update-restart");

            while (true)
            {
                // 自展开：首次运行（或标记缺失）时释放内置应用层
                if (!File.Exists(marker))
                {
                    Log("首次运行：释放内置运行文件到 " + AppDirName + "…");
                    ExtractApp(appDir);
                    File.WriteAllText(marker, DateTime.Now.ToString("s"), Encoding.UTF8);
                    Log("释放完成");
                }
                else
                {
                    Log("已展开，跳过释放（标记时间 " + File.ReadAllText(marker).Trim() + "）");
                }

                // 增量更新（清单比对 → 多源下载 → sha256 校验 → 原子替换 → 失败回滚）
                // 更新失败一律不阻断启动
                var urls = ManifestUrls();
                Log("更新清单源：" + string.Join(" | ", urls));
                await UpdateService.RunAsync(appDir, urls, Log);

                var exe = Path.Combine(appDir, AppExeName);
                if (!File.Exists(exe))
                {
                    Log("错误：找不到应用主程序 " + exe);
                    return 2;
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
