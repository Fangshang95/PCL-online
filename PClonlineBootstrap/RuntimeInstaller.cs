using System.Text.Json;

namespace PClonlineBootstrap;

/// <summary>
/// 运行环境检查（v50.11.7：CE 原版思路——只检测、只提示，不代用户下载安装）。
///
/// 背景与取舍：此前 v50.11.6 会用包内 net.zip 申请 UAC 把运行时自动装进
/// %ProgramFiles%\dotnet。实测这条路对玩家并不可靠（提权可能被拒、杀软拦 UAC、
/// 弱网下 70MB 包下不动、老版本残留难清理），而且**替玩家装系统组件本身超出了
/// 启动器该做的事**。因此改为 PCL CE 原版做法：
///
///   1. 自包含应用层（v50.10.x 老安装，运行时在包里）→ 放行；
///   2. 玩家系统里已有 .NET（apphost 能扫到的标准位置）→ 放行；
///   3. 都没有 → 返回空串，由引导器弹窗给出官方下载链接，玩家自己装。
///
/// **不联网下载、不提权安装、不写任何系统目录。** 缺环境时唯一的动作是弹窗告知。
///
/// apphost 只认三个运行时位置：DOTNET_ROOT 环境变量（提权会丢，已废弃）、
/// 注册表 InstallLocation（要管理员）、%ProgramFiles%\dotnet（要管理员）。
/// 免管理员的 %LOCALAPPDATA%\dotnet 实测**不在**查找路径（v50.11.6 用窗口枚举证实：
/// 主程序弹着缺 .NET 的 MessageBox 挂起），所以这里只认真实会被扫到的位置。
/// </summary>
internal static class RuntimeInstaller
{
    /// <summary>应用层声明的运行时（例如 dotnet 10.0.0 + WindowsDesktop 10.0.0）。</summary>
    private sealed record Requirement(string Name, string Version);

    /// <summary>官方 .NET 10 Desktop Runtime 下载页（缺环境时弹窗里给玩家）。</summary>
    public const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";

    /// <summary>
    /// 运行环境是否已就绪。
    ///
    /// 返回值：null = 就绪（apphost 自行解析，无需环境变量）；"" = 缺失，需要提示玩家自行下载安装。
    /// </summary>
    public static Task<string?> EnsureAsync(string appDir, string legacyRuntimeDir,
                                              UpdateManifest? manifest, Action<string> log)
    {
        // 0) 自包含应用层：运行时就在包里，放行
        if (IsSelfContained(appDir))
        {
            log("应用层自带运行时（自包含版），无需安装运行库");
            return Task.FromResult<string?>(null);
        }

        // 1) 系统里已装能用的运行时 → 放行（apphost 自己找得到，不需要环境变量）
        var systemRoot = FindSystemRuntime(appDir, log);
        if (systemRoot is not null)
        {
            log("使用系统运行时：" + systemRoot);
            return Task.FromResult<string?>(null);
        }

        // 2) 没有 = 玩家自己去装。启动器不联网下载、不提权写系统目录。
        log("未检测到可用的 .NET 运行时——请玩家自行下载安装后再启动");
        return Task.FromResult<string?>("");
    }

    /// <summary>
    /// "把应用层换成新版（无框架）之后还跑得起来吗？"——换 exe 升级前的保护性判断。
    /// 旧应用层可能是自包含版、直接能跑；盲目清掉再装不上运行时，玩家连旧版都没了。
    /// </summary>
    public static bool NewAppCanRun(string baseDir, string appDir)
    {
        // 自包含判断在 Program 侧（EmbeddedIsSelfContained）先行处理；
        // 这里是无框架应用层的可运行性：系统已装能用的 .NET 即可
        return FindSystemRuntime(appDir, static _ => { }) is not null;
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

    /// <summary>系统 dotnet 里挑一个满足应用层声明的目录；挑不到返回 null。</summary>
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

    /// <summary>
    /// apphost 会扫到的标准安装位置。要管理员写入，所以只认真实会被扫到的目录。
    /// 故意不列 %LOCALAPPDATA%\dotnet：实测 apphost **不**扫描它，列进来只会造成
    /// "引导器误判已装、主程序弹缺 .NET 框挂起"的假象。
    /// </summary>
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

    private static (int Major, int Minor) Split(string? version)
    {
        var parts = (version ?? "0.0").Split('.');
        int.TryParse(parts.Length > 0 ? parts[0] : "0", out var maj);
        int.TryParse(parts.Length > 1 ? parts[1] : "0", out var min);
        return (maj, min);
    }
}
