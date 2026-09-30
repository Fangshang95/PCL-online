using System.Text.Json;

namespace PCL.MCStudio;

/// <summary>
/// 本机开房环境：frpc 路径、frps 地址、Java、服务端根目录。
/// 全部可用 mcstudio/host.json 覆盖（frpcExe/frpsHost/frpsPort/javaExe/serverRoot）。
/// frpsHost 磁盘文件缺失时回落构建期内嵌配置（StudioSecretsDir，v50.9.6 自包含）；
/// 两者都无则为占位域名（开发机行为）。v50 S2 双层凭证：host.json 的 token=frps 传输层静态
/// token（泄露无害，授权不在这层）；房间级 frps_token 由 allocate 动态下发、经
/// frpc user 字段随 Login/NewProxy 到 frps 插件鉴权（只认活跃租约）。
/// </summary>
public sealed record HostEnvironment(
    string FrpcExe, string FrpsHost, int FrpsPort, string Token, string JavaExe, string ServerRoot)
{
    private const string DefaultFrpsHost = "tunnel.example.com";
    private const int DefaultFrpsPort = 7000;

    private static readonly string[] DefaultJavaCandidates =
    {
        @"E:\MCServer\runtime\java21\bin\java.exe", // MC 1.20.5+ 需要 Java 21
        @"E:\MCServer\java\bin\java.exe",           // Java 17 兜底（老版本用）
    };

    public static HostEnvironment Load(string? explicitPath = null)
    {
        // 便携化：默认跟随程序所在目录（分发版直接可用）；开发机 E:\MCServer 存在时优先保持旧行为
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var portableRoot = Path.Combine(baseDir, "mcstudio");
        var useDevLayout = Directory.Exists(@"E:\MCServer");

        var frpcExe = useDevLayout
            ? @"E:\MCServer\tools\frp\frpc.exe"
            : Path.Combine(portableRoot, "frpc", "frpc.exe");
        var frpsHost = DefaultFrpsHost;
        var frpsPort = DefaultFrpsPort;
        string? token = null;
        var javaExe = DefaultJavaCandidates.FirstOrDefault(File.Exists) ?? "java";
        var serverRoot = useDevLayout ? @"E:\MCServer\rooms" : Path.Combine(portableRoot, "rooms");

        var jsonPath = explicitPath ?? FindHostJson();
        // v50.9.6：磁盘 host.json 缺失时回落构建期内嵌配置（StudioSecretsDir 注入）——
        // 全新电脑/只换 exe 的机器零配置可用；磁盘文件存在时优先（用户可手改覆盖）
        string? jsonText = jsonPath is not null
            ? File.ReadAllText(jsonPath)
            : StudioSecrets.EmbeddedJson("host.json");
        if (jsonText is not null)
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            frpcExe = ReadString(root, "frpcExe") ?? frpcExe;
            frpsHost = ReadString(root, "frpsHost") ?? frpsHost;
            if (root.TryGetProperty("frpsPort", out var p) && p.TryGetInt32(out var pp)) frpsPort = pp;
            token = ReadString(root, "token") ?? token;
            javaExe = ReadString(root, "javaExe") ?? javaExe;
            serverRoot = ReadString(root, "serverRoot") ?? serverRoot;
        }

        // v50 S2：不再从 frpc.toml/.token.txt 读静态凭证（旧 token 已泄露作废）；
        // frps 凭证由 allocate 按房间动态下发，host.json 的 token 仅作应急兜底。
        return new HostEnvironment(frpcExe, frpsHost, frpsPort, token ?? "", javaExe, serverRoot);
    }

    private static string? FindHostJson()
    {
        string[] candidates =
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcstudio", "host.json"),
            Path.Combine(Environment.CurrentDirectory, "mcstudio", "host.json"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            && e.GetString() is { Length: > 0 } s
            ? s
            : null;
}
