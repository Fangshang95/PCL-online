using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PCL.MCStudio;

/// <summary>MC「对局域网开放」捕获到的世界（v48）：端口 + 世界名。</summary>
public sealed record LanWorldInfo(int Port, string Motd);

/// <summary>
/// LAN 公告侦测器（v48 虚拟局域网）：三源捕获 MC 集成服务器的局域网端口。
/// 主源 = 组播 224.0.2.60:4445（MC「对局域网开放」每 1.5s 广播 [MOTD]…[/MOTD][AD]port[/AD]），
/// 按本机每个非回环 IPv4 接口逐一加入组播组（多网卡/虚拟网卡环境防漏收）。
/// 兜底 1 = tail 实例 logs/latest.log（"Local game hosted on port N"），覆盖组播被安全软件拦截的场景。
/// 兜底 2（v50.9）= 游戏进程 TCP 监听端口差分（netstat -ano）——与 MC 版本日志格式、防火墙组播
/// 策略均无关：对局域网开放后集成服务器必然在本进程监听一个 TCP 端口。
/// 看门狗：当前世界 >15s 无公告且端口不在监听判定世界关闭，回调 null。
/// </summary>
public sealed class LanSniffer : IDisposable
{
    private static readonly IPAddress GroupAddr = IPAddress.Parse("224.0.2.60");
    private const int GroupPort = 4445;
    private static readonly Regex RePort =
        new(@"\[AD\](\d+)\[/AD\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReMotd =
        new(@"\[MOTD\](.*?)\[/MOTD\]", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    // v48.1：日志行两种形态——"Local game hosted on port 25565"（部分版本）与
    // "Started serving on 25565" / "Started serving on /0.0.0.0:25565"（1.20.1 Forge 实测）
    private static readonly Regex ReLogPort =
        new(@"(?:Local game hosted on port (?<p>\d+))|(?:Started serving on .*?(?<p>\d+)\s*$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);
    // v50.9：netstat -ano 监听行——"  TCP    0.0.0.0:50000    0.0.0.0:0    LISTENING    12345"
    // （状态字与语言区域无关，所有系统均为英文 LISTENING）
    private static readonly Regex ReNetstat =
        new(@"^\s*TCP\s+\S*?:(?<p>\d+)\s+\S+\s+LISTENING\s+(?<pid>\d+)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string? _logFile;
    private readonly Func<int?>? _pidProvider;
    private readonly List<Socket> _sockets = new();
    private CancellationTokenSource? _cts;
    private long _logOffset;
    private bool _multicastReady;
    private int _ifaceCount;

    private LanWorldInfo? _current;
    private DateTime _lastSeen = DateTime.MinValue;
    private readonly object _lock = new();

    /// <summary>捕获/关闭事件：world=null 表示当前世界已关闭（端口静默超时）。UI 线程外触发。</summary>
    public event Action<LanWorldInfo?>? Detected;

    /// <summary>诊断事件（v48.1）：侦测器内部状态，供 UI 状态区/日志展示，便于用户自查。</summary>
    public event Action<string>? Trace;

    public LanWorldInfo? Current { get { lock (_lock) return _current; } }

    public LanSniffer(string? logFile, Func<int?>? mcPidProvider = null)
    {
        _logFile = logFile;
        _pidProvider = mcPidProvider;
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        // v48.1：PCL 进程 UDP 入站放行（组播 4445 接收方是启动器自己；多 exe 部署下
        // 旧规则绑的是别的 exe 路径，按名称删掉重建）。非管理员时静默失败，走日志兜底。
        try
        {
            var exe = Environment.ProcessPath ?? "";
            if (exe.Length > 0)
            {
                RunNetsh($"advfirewall firewall delete rule name=\"MCStudio LAN\"");
                RunNetsh($"advfirewall firewall add rule name=\"MCStudio LAN\" dir=in action=allow " +
                         $"program=\"{exe}\" protocol=udp enable=yes profile=any");
                Trace?.Invoke("已放行启动器 UDP 入站（组播接收）");
            }
        }
        catch { /* 非管理员：静默，走日志兜底 */ }
        StartMulticast(ct);
        StartLogTail(ct);
        StartProcessPortWatch(ct);
        StartWatchdog(ct);
    }

    private static void RunNetsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        p?.WaitForExit(5000);
    }

    private void StartMulticast(CancellationToken ct)
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, GroupPort));
            var ifaces = new List<IPAddress>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                        || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        continue;
                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                            ifaces.Add(ip.Address);
                }
            }
            catch { /* 枚举失败按默认接口加入 */ }
            var membershipIps = ifaces.Count > 0 ? ifaces : [IPAddress.Any];
            foreach (var ip in membershipIps)
            {
                try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(GroupAddr, ip)); } catch { /* 接口不支持组播 */ }
            }
            socket.Blocking = false;
            _sockets.Add(socket);
            _multicastReady = true;
            _ifaceCount = membershipIps.Count;
            Trace?.Invoke($"组播监听已就绪（224.0.2.60:4445，{membershipIps.Count} 个网络接口加入组播组）");
            var buf = new byte[512];
            _ = Task.Run(async () =>
            {
                var remote = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        if (!socket.Poll(300_000, SelectMode.SelectRead)) { ct.ThrowIfCancellationRequested(); continue; }
                        var n = socket.ReceiveFrom(buf, ref remote);
                        Trace?.Invoke($"收到组播包 {n}B：{Encoding.UTF8.GetString(buf, 0, Math.Min(n, 120))}");
                        HandlePacket(buf, n);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* 单包失败继续 */ }
                }
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"组播监听不可用（{ex.Message}），依赖游戏日志兜底");
        }
    }

    private void HandlePacket(byte[] buf, int n)
    {
        var text = Encoding.UTF8.GetString(buf, 0, n);
        var m = RePort.Match(text);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var port) || port <= 0) return;
        var motd = ReMotd.Match(text) is { Success: true } mm ? mm.Groups[1].Value.Trim() : "";
        OnDetected(new LanWorldInfo(port, motd));
    }

    private void StartLogTail(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_logFile))
        {
            Trace?.Invoke("日志兜底未启用（日志路径未知）");
            return;
        }
        Trace?.Invoke($"日志兜底已启动：{_logFile}");
        _ = Task.Run(async () =>
        {
            // v48.1：从文件末尾开始只读新增——启动前残留的旧日志行不回放，防误报旧端口
            try
            {
                if (File.Exists(_logFile))
                    _logOffset = new FileInfo(_logFile).Length;
            }
            catch { _logOffset = 0; }
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(_logFile))
                    {
                        using var fs = new FileStream(_logFile, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        // 日志滚动/新启动后文件变小：从头重读
                        if (fs.Length < _logOffset) _logOffset = 0;
                        fs.Seek(_logOffset, SeekOrigin.Begin);
                        using var sr = new StreamReader(fs, Encoding.UTF8);
                        var text = await sr.ReadToEndAsync(ct).ConfigureAwait(false);
                        _logOffset = fs.Position;
                        var m = ReLogPort.Match(text);
                        if (m.Success && int.TryParse(m.Groups["p"].Value, out var p) && p > 0)
                        {
                            Trace?.Invoke($"日志捕获端口：{p}（来源 latest.log）");
                            OnDetected(new LanWorldInfo(p, ""));
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* 文件被锁等：下轮重试 */ }
                try { await Task.Delay(2000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// v50.9 第三源：游戏进程 TCP 监听端口差分。每 2s 解析 netstat -ano，取目标 PID 的
    /// LISTENING 端口；基线差分——仅「启动后新出现的监听端口」触发（单人未开放局域网时
    /// 集成服务器不监听，误报面为零）。游戏重启（PID 变化）自动重拍基线。
    /// </summary>
    private void StartProcessPortWatch(CancellationToken ct)
    {
        if (_pidProvider is null)
        {
            Trace?.Invoke("进程端口侦测未启用（游戏 PID 提供方缺失）");
            return;
        }
        Trace?.Invoke("进程端口侦测已启动（游戏进程 TCP 监听差分，兜底 2）");
        _ = Task.Run(async () =>
        {
            int? lastPid = null;
            var baseline = new HashSet<int>();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var pid = _pidProvider();
                    if (pid is int p && p > 0)
                    {
                        if (pid != lastPid)
                        {
                            // 新游戏进程（重开游戏）：重拍基线，基线内既有端口不触发
                            lastPid = pid;
                            baseline = ListPortsOf(p);
                            Trace?.Invoke($"进程端口侦测基线：PID {p}，现有监听 {baseline.Count} 个");
                        }
                        var ports = ListPortsOf(p);
                        var curPort = Current?.Port;
                        if (curPort is int cp) ports.Remove(cp);   // 已捕获端口不再触发
                        var fresh = ports.Except(baseline).OrderBy(x => x).ToList();
                        if (fresh.Count > 0)
                        {
                            Trace?.Invoke($"进程端口捕获：{fresh[0]}（PID {p}，来源 netstat 差分）");
                            OnDetected(new LanWorldInfo(fresh[0], ""));
                        }
                    }
                    else if (lastPid is not null)
                    {
                        lastPid = null;
                        baseline.Clear();
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* 单轮失败继续 */ }
                try { await Task.Delay(2000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, CancellationToken.None);
    }

    /// <summary>进程 PID 拥有的 LISTENING TCP 端口集合（netstat -ano 解析，无 WMI/PInvoke 依赖）。</summary>
    private static HashSet<int> ListPortsOf(int pid)
    {
        var result = new HashSet<int>();
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netstat", "-ano -p tcp")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            if (p is null) return result;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            foreach (var line in text.Split('\n'))
            {
                var m = ReNetstat.Match(line);
                if (m.Success && int.TryParse(m.Groups["pid"].Value, out var owner) && owner == pid
                    && int.TryParse(m.Groups["p"].Value, out var port) && port > 0)
                    result.Add(port);
            }
        }
        catch { /* netstat 不可用：该兜底源静默失效，其余两源仍在 */ }
        return result;
    }

    private void StartWatchdog(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    LanWorldInfo? cur; DateTime seen;
                    lock (_lock) { cur = _current; seen = _lastSeen; }
                    var silent = cur is not null && (DateTime.UtcNow - seen).TotalSeconds > 15;
                    if (silent && !TcpPortListening(cur!.Port))
                    {
                        // v48.5：判定关闭的硬条件 = 公告静默 15s **且** 系统里该端口已不在 TCP 监听——
                        // MC 集成服务器只要开着就必然监听端口，公告丢失（防火墙/组播抖动）不再误报
                        lock (_lock) _current = null;
                        try { Detected?.Invoke(null); } catch { /* 订阅方异常不外溢 */ }
                    }
                    else if (silent)
                    {
                        // 公告断了但端口仍在监听（用户仍在游戏）：静默续约，不打扰
                        lock (_lock) _lastSeen = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, CancellationToken.None);
    }

    /// <summary>系统 TCP 监听表里是否存在该端口（MC 集成服务器开着就必然监听；世界关闭即消失）。</summary>
    private static bool TcpPortListening(int port)
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(e => e.Port == port);
        }
        catch { return false; }
    }

    private void OnDetected(LanWorldInfo info)
    {
        lock (_lock)
        {
            if (_current is { } cur && cur.Port == info.Port)
            {
                _lastSeen = DateTime.UtcNow;   // 同端口续约（MOTD 可能变，跟随更新）
                if (cur.Motd == info.Motd) return;
                _current = info;
            }
            else
            {
                _current = info;
                _lastSeen = DateTime.UtcNow;
            }
        }
        try { Detected?.Invoke(info); } catch { /* 订阅方异常不外溢 */ }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        foreach (var s in _sockets)
            try { s.Dispose(); } catch { }
        _sockets.Clear();
    }
}

/// <summary>
/// 虚拟局域网房主会话（v48）：把「对局域网开放」的世界桥接进现有隧道体系。
/// 首个世界 → allocate + frpc(localPort=world.Port) + 注册(mode=lan) + P2P HostService；
/// 换世界/端口变化 → frpc 重开（allocate 幂等同远程端口，朋友端无感知）+ re-register + 端口热取；
/// 世界关闭（v50.6.1）→ 注销房间（好友立即不可加入）；下次开放世界自动重建、房间码换新。
/// 房间为心跳租约（hb）：会话存活期间每 55s 续期；断电/杀进程等未注销场景由云端 3 分钟兜底回收。
/// 数据面：P2P（KCP/IPv6）优先，frp 中转兜底（2MB/s 限速）——目标端口全部动态解析。
/// </summary>
public sealed class LanHostSession : IDisposable
{
    private readonly StudioApiClient _api;
    private readonly string _frpcExe, _frpsHost, _token;
    private readonly int _frpsPort;
    private FrpcManager? _frpc;
    private P2pTunnel.HostService? _p2p;
    private LanWorldInfo? _bridged;
    private string? _code;
    private int _remotePort;
    private string? _workDir;
    private string? _ownerToken;
    private CancellationTokenSource? _hbCts;

    public string? RoomCode => _code;
    public string? Address => _code is null ? null : $"{_frpsHost}:{_remotePort}";
    public LanWorldInfo? World => _bridged;

    /// <summary>桥接进度文案（隧道建立/热切换等）。UI 线程外触发。</summary>
    public event Action<string>? Progress;

    /// <param name="frpsToken">frps 固定凭证（host.json/frpc.toml 模板读取，供 FrpcManager 认证）。
    /// P2P 信令用玩家 Bearer token，由 BridgeAsync 的 ownerToken 传入，两者不可混用。</param>
    public LanHostSession(StudioApiClient api, string frpcExe, string frpsHost, int frpsPort, string frpsToken)
    {
        _api = api;
        _frpcExe = frpcExe;
        _frpsHost = frpsHost;
        _frpsPort = frpsPort;
        _token = frpsToken;
    }

    /// <summary>桥接一个世界（幂等 + 热切换）。mc/loader/mods 用于房间注册与朋友端版本匹配；
    /// packs 为 v50.8 包身份（整合包 + 资源类身份，&lt;1KB），随房间下发供玩家端原生安装同款。
    /// v50.9.4：逐文件清单（v50.6.9/v50.7 的 mrpack index）整体移除——对齐只走包身份 + 加入端
    /// 自动装配兜底（AutoAssembleLocalPackAsync），云载荷从 O(文件数) 变为常数 &lt;1KB。</summary>
    public async Task BridgeAsync(LanWorldInfo world, string mc, string loader,
        IReadOnlyList<ModEntry> mods, string? ownerToken, CancellationToken ct,
        RoomPacks? packs = null)
    {
        var old = _bridged;
        if (old is not null && old.Port == world.Port && _code is not null)
        {
            // 同端口重复捕获：只刷新注册（motd 可能变化），不动隧道
            await _api.RegisterRoomAsync(RoomName(), mc, loader, mods,
                ownerToken, ct, lan: new LanInfo(world.Port, world.Motd), hb: true,
                packs: packs).ConfigureAwait(false);
            StartHeartbeat(ownerToken);
            _bridged = world;
            return;
        }

        // frpc：首次建立 / 端口变化重开（allocate 幂等 → remotePort 与房间码不变）
        _frpc ??= new FrpcManager(_api, _frpcExe, _frpsHost, _frpsPort, _token);
        var workDir = _workDir ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "mcstudio", "lan", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var remotePort = _remotePort;
        if (_code is null)
        {
            Progress?.Invoke($"建立虚拟局域网隧道（本地端口 {world.Port}）…");
            (_remotePort, _) = await _frpc.OpenTunnelAsync(RoomName(), world.Port, workDir, ct)
                .ConfigureAwait(false);
        }
        else
        {
            // v48.7：RestartTunnel 不释放租约（allocate 幂等 → remotePort 不变），朋友端地址零漂移
            Progress?.Invoke($"世界端口变化 {old?.Port} → {world.Port}，切换隧道…");
            await _frpc.RestartTunnelAsync(RoomName(), world.Port, workDir, ct).ConfigureAwait(false);
            _remotePort = remotePort;   // 中转端口不变，房间地址无需更新
        }

        // 注册/更新房间（mode=lan；同房间码幂等）+ P2P 服务（端口热取）
        // v50.9.4：不再生成/下发逐文件清单；无包身份时朋友端走自动装配兜底装基础客户端
        Progress?.Invoke("注册虚拟局域网房间…");
        _code = await _api.RegisterRoomAsync(RoomName(), mc, loader, mods,
            ownerToken, ct, lan: new LanInfo(world.Port, world.Motd),
            hb: true, packs: packs).ConfigureAwait(false);
        StartHeartbeat(ownerToken);
        if (_p2p is null && !string.IsNullOrEmpty(ownerToken))
        {
            _p2p = new P2pTunnel.HostService(_api, ownerToken, _code, world.Port,
                portProvider: () => _bridged?.Port ?? world.Port);
            P2pTunnel.EnsureFirewallRule("");
        }
        _bridged = world;
        Progress?.Invoke($"房间码 {_code} · 中转 {_frpsHost}:{_remotePort} · P2P 直连已就绪");
    }

    /// <summary>v50.6.1 心跳租约：会话存续期间每 55s 续期 hb 房间（云端 TTL 180s）。
    /// 房间被云端回收（断网过久等）时报告并停跳；网络错误静默等下一轮。
    /// 重复调用幂等：重启循环并刷新令牌（热切换后同房间继续跳）。</summary>
    private void StartHeartbeat(string? ownerToken)
    {
        if (string.IsNullOrEmpty(ownerToken)) return;
        _ownerToken = ownerToken;
        try { _hbCts?.Cancel(); } catch { }
        try { _hbCts?.Dispose(); } catch { }
        var cts = _hbCts = new CancellationTokenSource();
        var room = RoomName();
        var token = ownerToken;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(55), cts.Token).ConfigureAwait(false);
                    if (_code is null) continue;    // 房间已注销，等重建后再跳
                    try
                    {
                        if (await _api.HeartbeatAsync(room, token, cts.Token).ConfigureAwait(false) is null)
                        {
                            Progress?.Invoke("⚠ 云端房间已失效（与服务器失联过久），房间码不再可用；" +
                                             "重新对局域网开放将自动开新房间");
                            cts.Cancel();
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* 网络抖动：下一轮重试 */ }
                }
            }
            catch (OperationCanceledException) { /* 关房退出 */ }
        });
    }

    private string? _roomName;
    private string RoomName()
        => _roomName ??= "lan-" + Guid.NewGuid().ToString("N")[..12];

    private static string ThrowRoomMissing() =>
        throw new InvalidOperationException("内部错误：LAN 房间名未初始化");

    /// <summary>结束联机/世界关闭：停心跳、停 P2P、关隧道、主动注销房间码（好友立即不可加入）。
    /// release 失败（断网等）时由云端心跳 TTL 3 分钟内兜底回收。重复调用幂等。</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        var room = _code is null ? null : RoomName();
        var token = _ownerToken;
        try { _hbCts?.Cancel(); } catch { }
        try { _hbCts?.Dispose(); } catch { }
        _hbCts = null;
        try { _p2p?.Dispose(); } catch { }
        _p2p = null;
        if (_frpc is not null && _code is not null)
        {
            try { await _frpc.CloseTunnelAsync(RoomName(), _workDir ?? "").ConfigureAwait(false); }
            catch { /* 网络问题时等 TTL 回收 */ }
        }
        _frpc?.Dispose();
        _frpc = null;
        _code = null;
        _bridged = null;
        _remotePort = 0;
        if (room is not null && !string.IsNullOrEmpty(token))
        {
            try { await _api.ReleaseAsync(room, null, CancellationToken.None, token).ConfigureAwait(false); }
            catch { /* 网络问题时等 TTL 回收 */ }
        }
    }

    /// <summary>v50.6.1 世界关闭/游戏退出时注销房间：好友立即不可加入，房间码作废。
    /// 会话保留可复用——玩家重新对局域网开放时 BridgeAsync 自动重建（新租约、新房间码）。
    /// 实现＝StopAsync（关隧道 + 主动 release），重复调用幂等。</summary>
    public Task DropRoomAsync(CancellationToken ct = default) => StopAsync(ct);

    /// <summary>尽力注销（Dispose 路径兜底）：同步释放本地资源 + 后台尽力发 release；
    /// 网络失败由云端心跳 TTL 兜底，不阻塞调用方。</summary>
    public void Dispose()
    {
        try { _hbCts?.Cancel(); } catch { }
        try { _p2p?.Dispose(); } catch { }
        try { _frpc?.Dispose(); } catch { }
        if (_code is not null && !string.IsNullOrEmpty(_ownerToken))
        {
            var room = RoomName();
            var token = _ownerToken;
            _ = Task.Run(async () =>
            {
                try { await _api.ReleaseAsync(room, null, CancellationToken.None, token).ConfigureAwait(false); }
                catch { /* 尽力而为：失败由云端 TTL 兜底 */ }
            });
        }
    }
}
