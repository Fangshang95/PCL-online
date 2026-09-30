using System.Diagnostics;
using System.IO;
using System.Text;

namespace PCL.MCStudio;

/// <summary>
/// frpc 生命周期管理：分配端口 → 生成 frpc.toml → 启动 → PID 精确停止。
/// 逻辑与已验证的 Python CLI 完全一致（凭证从文件读取、绝不按映像名杀进程）。
/// v50 S2：frps 凭证改为 allocate 下发的房间级临时 token（随租约生成/释放，
/// frps 插件鉴权只认活跃租约），静态 token 仅作旧服务端兜底。
/// </summary>
public sealed class FrpcManager : IDisposable
{
    private readonly StudioApiClient _api;
    private readonly string _frpcExe;
    private readonly string _serverAddr;
    private readonly int _serverPort;
    private readonly string _token;

    private readonly Dictionary<string, Process> _procs = new(); // room -> frpc 进程
    private readonly Dictionary<string, string> _leaseTokens = new(); // room -> 租约释放凭证

    private static bool _orphansSwept;

    /// <summary>
    /// v50.6.3：清扫历史会话遗留的孤儿 frpc（20260925 实测根因）。PCL 崩溃/强杀时
    /// Dispose 不会跑，frpc 常驻连着 frps 占住端口池端口 → allocate 复用该端口 →
    /// 房主 frpc 注册永远 'port already used' → 中转整段不可用（朋友端游戏内表现为
    /// 「已建立的连接被您的主机上的软件中止」）。本机 20260925 就有两个孤儿挂了 5 小时。
    /// 依据 frpc.pid 精确击杀（绝不按映像名杀）：仅本 exe 目录 mcstudio\lan\ 下的
    /// PID 文件——正常房间隧道的宿主就是本进程，启动时仍存活的必然是孤儿。
    /// 构造函数触发（进程内一次）；云端 tunnel-api 孤儿对账作第二道防线。
    /// </summary>
    public static void CleanupOrphans()
    {
        if (_orphansSwept) return;
        _orphansSwept = true;
        try
        {
            var lanRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcstudio", "lan");
            if (!Directory.Exists(lanRoot)) return;
            foreach (var pidFile in Directory.EnumerateFiles(lanRoot, "frpc.pid", SearchOption.AllDirectories))
            {
                try
                {
                    if (int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
                    {
                        var p = Process.GetProcessById(pid);
                        if (!p.HasExited && p.ProcessName.Equals("frpc", StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill(entireProcessTree: true);
                            Console.WriteLine($"[FrpcManager] 已清扫孤儿 frpc（pid={pid}，来源 {pidFile}）");
                        }
                    }
                }
                catch (ArgumentException) { /* 进程已不存在 */ }
                catch (Exception) { /* 单个目标失败不影响其余 */ }
                try { File.Delete(pidFile); } catch { /* 删不掉下次再试 */ }
            }
        }
        catch { /* 清扫失败不阻塞开房：云端对账兜底 */ }
    }

    public FrpcManager(StudioApiClient api, string frpcExe, string serverAddr, int serverPort, string token)
    {
        CleanupOrphans();
        _api = api;
        _frpcExe = frpcExe;
        _serverAddr = serverAddr;
        _serverPort = serverPort;
        _token = token;
    }

    /// <summary>为房间建立隧道：申请端口 → 写配置 → 启动 frpc。返回 (远程端口, 房间码)。</summary>
    public async Task<(int RemotePort, string RoomCode)> OpenTunnelAsync(
        string room, int localPort, string workDir, CancellationToken ct = default)
    {
        var alloc = await _api.AllocateAsync(room, ct).ConfigureAwait(false);
        int remotePort = alloc.RemotePort;
        RememberLease(room, alloc);
        await WriteConfigAndStartAsync(room, localPort, workDir, remotePort,
            alloc.FrpsToken, ct).ConfigureAwait(false);
        string roomCode = alloc.RoomCode ?? $"MC{remotePort % 1000:D3}";
        return (remotePort, roomCode);
    }

    private void RememberLease(string room, AllocateResult alloc)
    {
        if (!string.IsNullOrEmpty(alloc.LeaseToken))
            _leaseTokens[room] = alloc.LeaseToken;
        else
            _leaseTokens.Remove(room);
    }

    /// <summary>写 frpc.toml 并启动进程（Open/Restart 共用）。
    /// v50 S2 双层凭证：auth.token=静态传输 token（host.json，frps 传输层校验，
    /// 泄露无害——授权不在这一层）；user=allocate 下发的房间级 frps_token
    /// （frps 插件鉴权只认活跃租约，旧服务端无此字段则不写 user）。</summary>
    private async Task WriteConfigAndStartAsync(string room, int localPort, string workDir,
        int remotePort, string roomFrpsToken, CancellationToken ct)
    {
        var dir = Directory.CreateDirectory(workDir);
        var cfgPath = Path.Combine(dir.FullName, "frpc.toml");
        var sb = new StringBuilder();
        sb.AppendLine($"serverAddr = \"{_serverAddr}\"");
        sb.AppendLine($"serverPort = {_serverPort}");
        sb.AppendLine($"auth.token = \"{_token}\"");
        if (!string.IsNullOrEmpty(roomFrpsToken))
            sb.AppendLine($"user = \"{roomFrpsToken}\"");
        sb.AppendLine("loginFailExit = false");
        sb.AppendLine();
        sb.AppendLine("[[proxies]]");
        sb.AppendLine($"name = \"mcstudio-{room}-{remotePort}\"");
        sb.AppendLine("type = \"tcp\"");
        sb.AppendLine("localIP = \"127.0.0.1\"");
        sb.AppendLine($"localPort = {localPort}");
        sb.AppendLine($"remotePort = {remotePort}");
        sb.AppendLine("transport.bandwidthLimit = \"2MB\""); // 免费档默认限速，付费档可移除
        await File.WriteAllTextAsync(cfgPath, sb.ToString(), ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = _frpcExe,
            Arguments = $"-c \"{cfgPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("frpc 启动失败");
        _procs[room] = proc;

        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "frpc.pid"), proc.Id.ToString(), ct).ConfigureAwait(false);
        await Task.Delay(3000, ct).ConfigureAwait(false); // 等隧道握手
        if (proc.HasExited)
            throw new InvalidOperationException($"frpc 启动后立即退出（exit={proc.ExitCode}），检查端口占用/frps 状态");
    }

    /// <summary>停止房间隧道（按保存的进程对象精确停止，不影响其他 frpc 实例）并释放租约。</summary>
    public async Task CloseTunnelAsync(string room, string workDir)
    {
        if (_procs.TryGetValue(room, out var proc) && !proc.HasExited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            _procs.Remove(room);
        }
        else
        {
            // 跨会话恢复：读 PID 文件精确击杀
            var pidFile = Path.Combine(workDir, "frpc.pid");
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    if (!p.HasExited && p.ProcessName.Equals("frpc", StringComparison.OrdinalIgnoreCase))
                        p.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { /* 进程已不存在 */ }
            }
        }
        var pidF = Path.Combine(workDir, "frpc.pid");
        if (File.Exists(pidF)) File.Delete(pidF);
        try { await _api.ReleaseAsync(room, _leaseTokens.GetValueOrDefault(room)).ConfigureAwait(false); }
        catch { /* 网络问题时等 TTL 自动回收 */ }
        _leaseTokens.Remove(room);
    }

    /// <summary>
    /// 重开房间隧道（v48.7 虚拟局域网热切换专用）：只杀 frpc 进程并按新 localPort 重写配置重启，
    /// 【不释放租约】—— allocate 幂等保证远程端口不变，朋友端地址零漂移。
    /// （CloseTunnelAsync 会 ReleaseAsync 释放租约，重 allocate 可能换端口 → 朋友端 Connection reset）
    /// </summary>
    public async Task RestartTunnelAsync(
        string room, int localPort, string workDir, CancellationToken ct = default)
    {
        // 杀旧 frpc 进程（保留租约）
        if (_procs.TryGetValue(room, out var proc) && !proc.HasExited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            _procs.Remove(room);
        }
        else
        {
            var pidFile = Path.Combine(workDir, "frpc.pid");
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
            {
                try
                {
                    var old = Process.GetProcessById(pid);
                    if (!old.HasExited && old.ProcessName.Equals("frpc", StringComparison.OrdinalIgnoreCase))
                        old.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
            }
        }
        await Task.Delay(500, ct).ConfigureAwait(false);   // 等端口/连接释放

        // allocate 幂等：租约存活时返回同一远程端口（顺带续期；凭证随租约不变）
        var alloc = await _api.AllocateAsync(room, ct).ConfigureAwait(false);
        int remotePort = alloc.RemotePort;
        RememberLease(room, alloc);
        await WriteConfigAndStartAsync(room, localPort, workDir, remotePort,
            alloc.FrpsToken, ct).ConfigureAwait(false);
    }

    public FrpcState GetState(string room)
    {
        if (_procs.TryGetValue(room, out var proc))
            return new FrpcState(!proc.HasExited, proc.Id, "");
        return new FrpcState(false, null, "");
    }

    public void Dispose()
    {
        foreach (var p in _procs.Values)
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}
