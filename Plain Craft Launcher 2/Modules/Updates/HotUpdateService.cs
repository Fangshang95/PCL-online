using System.IO;
using System.Text.Json;
using PCL.Core.Utils;
using PCL.Network;

namespace PCL;

/// <summary>
/// v50.10 热更新链路的应用层视图。
///
/// 与引导器共用同一份 version.json 清单，但这里只做「检查与提示」——app\ 里放的是
/// 正在运行的程序本体，Windows 锁着这些文件，运行期无法自我替换。真正的下载、
/// 验签、替换由引导器在应用退出后完成：应用写 .update-restart 标记再正常退出，
/// 等待中的引导器看到标记就重新执行「更新检查 → 拉起应用」。
///
/// 清单源与引导器保持一致（引导器支持 PCL_UPDATE_MANIFEST 覆盖，环境变量会被
/// 子进程继承，所以这里读同一个变量即可保持一致）。
/// </summary>
public static class HotUpdateService
{
    /// <summary>
    /// 更新清单只走 GitHub Releases（唯一源）。自建更新服务器已从客户端整体移除，
    /// 之前它只是个"闲时兜底"，现在改由 GitHub 承担。
    /// </summary>
    private static readonly string[] DefaultManifestUrls =
    [
        "https://github.com/Fangshang95/PCL-online/releases/latest/download/version.json",
    ];

    /// <summary>应用层的版本标记：引导器自展开与每次更新后都会写 app\version.json。</summary>
    public static string AppVersionFile =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.json");

    /// <summary>当前应用层版本（读不到清单时退回程序内置版本号，即未走热更新的老安装）。</summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                if (File.Exists(AppVersionFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(AppVersionFile));
                    if (doc.RootElement.TryGetProperty("version", out var v))
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
            catch
            {
                // 清单损坏按"未版本化"处理，退回内置版本号
            }
            return ModBase.versionBaseName;
        }
    }

    public static string[] ManifestUrls()
    {
        var env = Environment.GetEnvironmentVariable("PCL_UPDATE_MANIFEST");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var list = env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (list.Length > 0) return list;
        }
        return DefaultManifestUrls;
    }

    public sealed record CheckResult(
        bool Ok, string LocalVersion, string RemoteVersion,
        bool HasUpdate, long DownloadSize, bool Incremental, string Message);

    /// <summary>拉清单并判断有没有更新。只读网络与本地版本，不改任何文件。</summary>
    public static async Task<CheckResult> CheckAsync()
    {
        var local = CurrentVersion;
        foreach (var url in ManifestUrls())
        {
            try
            {
                var text = await Requester.FetchStringAsync(url, RequestParam.WithRetry);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("version", out var v)) continue;
                var remote = v.GetString();
                if (string.IsNullOrWhiteSpace(remote)) continue;

                var hasUpdate = IsNewer(remote, local);
                var (size, incremental) = PickPackage(root, local);
                return new CheckResult(true, local, remote, hasUpdate, size, incremental,
                    hasUpdate ? "" : "已是最新版本");
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[HotUpdate] 清单源不可用：" + url);
            }
        }
        return new CheckResult(false, local, "", false, 0, false,
            "暂时获取不到更新清单（不影响使用，稍后再试）");
    }

    private static bool IsNewer(string remote, string local)
    {
        try
        {
            return SemVer.Parse(remote) > SemVer.Parse(local);
        }
        catch
        {
            // 版本号不是合法语义版本时退化为"不相同即更新"
            return !string.Equals(remote, local, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>选包展示用：v50.11 起只有一种包——全量无框架版（app.zip），整体覆盖 app\。</summary>
    private static (long Size, bool Incremental) PickPackage(JsonElement root, string local)
    {
        if (root.TryGetProperty("packages", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in ps.EnumerateArray())
            {
                var type = p.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (!string.Equals(type, "full", StringComparison.OrdinalIgnoreCase)) continue;
                var size = p.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                return (size, false);
            }
        }
        return (0, false);
    }

    /// <summary>
    /// 请求"退出并更新"：写重启标记后由调用方正常退出程序。
    /// 等待中的引导器会据此重新执行更新检查（此时文件锁已全部释放），完成后再拉起应用。
    /// 返回 false 表示标记没写成（例如目录只读），调用方应提示用户手动重启。
    /// </summary>
    public static bool RequestRestartToUpdate()
    {
        try
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".update-restart"),
                DateTimeOffset.Now.ToString("s"));
            ModBase.Log("[HotUpdate] 已写重启标记，应用退出后引导器将自动完成更新");
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[HotUpdate] 写入重启标记失败");
            return false;
        }
    }
}
