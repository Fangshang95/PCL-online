using System.Text;

namespace PCL.MCStudio;

/// <summary>
/// P2P 复盘日志（v46.2）：NAT 探测 / IPv6 直连 / KCP 打洞 / 回退决策全链路落盘，
/// 便于离线复盘「P2P 为什么没打通」。按天滚动 {exe}\mcstudio\logs\p2p-yyyyMMdd.log，
/// UTF-8 追加写；任何 I/O 失败静默（不影响主流程）。
/// 行格式：HH:mm:ss.fff 内容；会话以「────」分隔块开头，终局 result 与埋点口径一致
/// （v6 / lan / udp / no_peer / skipped / timeout / relay-fallback / relay …）。
/// </summary>
public static class P2pTrace
{
    private static readonly object Lock = new();

    /// <summary>会话开始：分隔块 + 上下文（角色 / 房间 / 模式与候选概况）。</summary>
    public static void Session(string role, string code, string detail)
        => Write($"──── P2P 尝试 [{role}] 房间 {code}{(string.IsNullOrEmpty(detail) ? "" : " · " + detail)}");

    /// <summary>过程行：NAT 判定、候选逐个尝试、打洞窗口、决策原因等。</summary>
    public static void Log(string line) => Write(line);

    /// <summary>会话终局：result 与埋点口径一致；detail 补充耗时 / 线路地址。</summary>
    public static void End(string role, string result, string detail = "")
        => Write($"──── P2P 终局 [{role}] result={result}{(string.IsNullOrEmpty(detail) ? "" : " · " + detail)}");

    private static void Write(string line)
    {
        try
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcstudio", "logs");
            var file = Path.Combine(dir, $"p2p-{DateTime.Now:yyyyMMdd}.log");
            lock (Lock)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* 复盘日志失败不影响主流程 */ }
    }
}
