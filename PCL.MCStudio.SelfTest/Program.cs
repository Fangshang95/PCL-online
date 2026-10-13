using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PCL.MCStudio;

// 控制台输出统一 UTF-8，避免 Windows GBK 控制台/管道下中文乱码
Console.OutputEncoding = Encoding.UTF8;

// 用法: dotnet run --project PCL.MCStudio.SelfTest -- <api.json路径> [--frpc]
//       dotnet run --project PCL.MCStudio.SelfTest -- --auth <tunnel-api 地址>（仅测账号/好友链路）
//       dotnet run --project PCL.MCStudio.SelfTest -- --e2e <api.json路径>（一键开房 + 加入装配全链路）
//       dotnet run --project PCL.MCStudio.SelfTest -- --vanilla <minecraft目录>（仅测 vanilla 房间装配）
//       dotnet run --project PCL.MCStudio.SelfTest -- --tls <https地址> <根证书PEM路径>
//         （只测证书判定：拿指定的根去验那台服务器，供 _test_tls_server.py 驱动各场景）
var e2eIdx = Array.IndexOf(args, "--e2e");
if (e2eIdx >= 0 && e2eIdx + 1 < args.Length)
{
    await RunE2ESelfTest(args[e2eIdx + 1]);
    return;
}
var vanillaIdx = Array.IndexOf(args, "--vanilla");
if (vanillaIdx >= 0 && vanillaIdx + 1 < args.Length)
{
    await RunVanillaCheck(args[vanillaIdx + 1]);
    return;
}
var authIdx = Array.IndexOf(args, "--auth");
if (authIdx >= 0 && authIdx + 1 < args.Length)
{
    await RunAuthSelfTest(args[authIdx + 1]);
    return;
}
var tlsIdx = Array.IndexOf(args, "--tls");
if (tlsIdx >= 0 && tlsIdx + 2 < args.Length)
{
    Environment.ExitCode = await RunTlsCheck(args[tlsIdx + 1], args[tlsIdx + 2]);
    return;
}

var cfgPath = args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? throw new ArgumentException("请传入 .api.json 路径");
var withFrpc = args.Contains("--frpc");

var cfg = StudioConfig.Load(cfgPath);
Console.WriteLine($"[*] 平台 API: {cfg.BaseUrl}");
using var api = new StudioApiClient(cfg);

// 1) 端口租约（幂等）
var alloc = await api.AllocateAsync("sdk-selftest");
Console.WriteLine($"[1] allocate   : port={alloc.RemotePort} addr={alloc.Address} lease={alloc.LeaseHours}h");

// 2) 注册房间（幂等，房间码可复验）
var mods = new List<ModEntry> { new("lithium", "mc1.21.1-0.15.4-fabric", "lithium-fabric-0.15.4+mc1.21.1.jar") };
var code = await api.RegisterRoomAsync("sdk-selftest", "1.21.1", "fabric", mods);
Console.WriteLine($"[2] register   : code={code}");
var code2 = await api.RegisterRoomAsync("sdk-selftest", "1.21.1", "fabric", mods);
if (code2 != code) throw new Exception($"[x] 幂等失败: {code} != {code2}");
Console.WriteLine($"    幂等复验   : 同房间重复注册返回同码 {code2} ✔");

// 3) 公开解析（模拟朋友端）
var info = await api.ResolveAsync(code);
Console.WriteLine($"[3] resolve    : {info.Loader} {info.Mc} @ {info.Address} mods={info.Mods.Count} expires_in={info.ExpiresIn}s");

if (withFrpc)
{
    // 4) frpc 隧道全链路（真实 frps；v50 凭证由 allocate 按房间动态下发）
    var env = HostEnvironment.Load();
    var workDir = Path.Combine(Path.GetTempPath(), "mcstudio-sdk-test");
    using var frpc = new FrpcManager(api, env.FrpcExe, env.FrpsHost, env.FrpsPort, env.Token);
    var (remotePort, roomCode) = await frpc.OpenTunnelAsync("sdk-selftest", 25581, workDir);
    Console.WriteLine($"[4] frpc 隧道  : {remotePort} code={roomCode}");

    using var sock = new TcpClient();
    await sock.ConnectAsync(env.FrpsHost, remotePort);
    Console.WriteLine($"    外网连通   : {env.FrpsHost}:{remotePort} ✔");

    await frpc.CloseTunnelAsync("sdk-selftest", workDir);
    Console.WriteLine($"[5] 隧道关闭   : frpc 停止 + 租约释放 ✔");
}
else
{
    await api.ReleaseAsync("sdk-selftest");
    Console.WriteLine("[4] release    : 租约已释放 ✔");
}

var rooms = await api.RoomsAsync();
var count = rooms.GetProperty("rooms").EnumerateObject().Count();
Console.WriteLine($"[6] 收尾校验   : 云端剩余租约 {count} 条（应为 0）");
Console.WriteLine("== SDK 自测全部通过 ==");

// 一键开房 + 加入装配 全链路自测（真实 frps + 云 tunnel-api + 本机 Java 服务端）
async Task RunE2ESelfTest(string apiJsonPath)
{
    var cfg = StudioConfig.Load(apiJsonPath);
    Console.WriteLine($"[*] 平台 API: {cfg.BaseUrl}");
    using var api = new StudioApiClient(cfg);

    var env = HostEnvironment.Load();
    if (!File.Exists(env.FrpcExe))
        throw new Exception($"[x] 未找到 frpc.exe: {env.FrpcExe}");
    if (string.IsNullOrEmpty(env.Token))
        throw new Exception("[x] frpc token 为空（读取 frpc.toml / .token.txt 失败）");
    if (!File.Exists(env.JavaExe))
        throw new Exception($"[x] 未找到 Java: {env.JavaExe}");

    var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var roomName = $"e2e{stamp % 100000000}";
    var workRoot = Path.Combine(Path.GetTempPath(), "mcstudio-e2e", roomName);
    var serverDir = Path.Combine(workRoot, "server");
    var mcDir = Path.Combine(workRoot, "minecraft");
    Directory.CreateDirectory(serverDir);
    Directory.CreateDirectory(mcDir);
    Console.WriteLine($"[*] 工作目录 : {workRoot}");
    Console.WriteLine($"[*] frpc      : {env.FrpcExe}");
    Console.WriteLine($"[*] java      : {env.JavaExe}");

    var progress = new Progress<HostProgress>(p => Console.WriteLine($"      [房主] {p.Step,-11} {p.Detail}"));
    var assembleProgress = new Progress<AssembleProgress>(p => Console.WriteLine($"      [朋友] {p.Step,-11} {p.Detail}"));

    var manager = new HostRoomManager(api, env.FrpcExe, env.FrpsHost, env.FrpsPort, env.Token);
    try
    {
        // 0) 开房前先给服务端装一个模组（Lithium），登记进房间清单
        var mod = await HostRoomManager.InstallServerModAsync(serverDir, "1.21.1", "fabric", "lithium");
        Console.WriteLine($"[0] 服务端模组 : {mod.Project} {mod.Version} -> {mod.Filename}");

        // 1) 一键开房：服务端 jar → Java 启动等 Done → frpc 隧道 → 注册房间码
        Console.WriteLine("[1] 一键开房   : 开始");
        var room = await manager.StartAsync(new HostOptions
        {
            Mc = "1.21.1",
            Loader = "fabric",
            ServerDir = serverDir,
            JavaExe = env.JavaExe,
            RoomName = roomName,
            MaxMemoryMb = 1536,
        }, progress);
        Console.WriteLine($"[1] 一键开房   : 码={room.Code} 地址={room.Address} 本地端口={room.LocalPort}");

        // 2) 外网连通校验（frps 中转可达）
        using (var sock = new TcpClient())
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await sock.ConnectAsync(env.FrpsHost, room.RemotePort, cts.Token);
            Console.WriteLine($"[2] 外网连通   : {room.Address} ✔");
        }

        // 3) 朋友端：解析房间码 → 自动装配实例（版本 json 烤入直连 + 客户端 jar + 依赖库 + 模组同步）
        Console.WriteLine("[3] 加入装配   : 解析房间码");
        var info = await api.ResolveAsync(room.Code);
        var assembled = await new RoomAssembler().AssembleAsync(info, mcDir, assembleProgress);
        Console.WriteLine($"[3] 加入装配   : 实例 {assembled.InstanceId} 模组 {assembled.Mods} 个 新依赖 {assembled.NewLibraries} 个");

        // 4) 装配产物校验：版本 json 烤入 --quickPlayMultiplayer 直连参数，模组已落到实例 mods/
        var instJson = Path.Combine(assembled.InstanceDir, assembled.InstanceId + ".json");
        var jsonText = await File.ReadAllTextAsync(instJson);
        if (!jsonText.Contains("--quickPlayMultiplayer") || !jsonText.Contains(room.Address))
            throw new Exception("[x] 实例 json 未烤入 --quickPlayMultiplayer 直连参数");
        var modsDir = Path.Combine(assembled.InstanceDir, "mods");
        var modFiles = Directory.Exists(modsDir) ? Directory.EnumerateFiles(modsDir, "*.jar").ToArray() : Array.Empty<string>();
        if (modFiles.Length == 0)
            throw new Exception("[x] 实例 mods/ 为空，模组未同步");

        // 回归校验：LWJGL 等 native 库必须已下载（此前 classifier 被 [:3] 截断，native jar 全部缺失，启动即崩）
        var libsRoot = Path.Combine(mcDir, "libraries");
        var nativeJars = Directory.Exists(libsRoot)
            ? Directory.EnumerateFiles(libsRoot, "*-natives-windows*.jar", SearchOption.AllDirectories).ToList()
            : new List<string>();
        if (nativeJars.Count == 0)
            throw new Exception("[x] 缺失 LWJGL/原生 natives 库（classifier 下载回归）");
        var totalLibs = Directory.EnumerateFiles(libsRoot, "*.jar", SearchOption.AllDirectories).Count();
        Console.WriteLine($"[4] 产物校验   : quickPlayMultiplayer 已烤入，mods={modFiles.Length} 个，库={totalLibs} 个（含 natives {nativeJars.Count} 个）");

        // 5) 关房：优雅停服 → 关隧道 → 释放租约
        await manager.StopAsync(progress);
        Console.WriteLine("[5] 关房       : 服务端已停、隧道已关、租约已释放");

        // 6) 收尾校验：云端该房间租约应为 0
        var rooms = await api.RoomsAsync();
        var left = rooms.GetProperty("rooms").EnumerateObject()
            .Count(kv => kv.Name == roomName);
        if (left != 0)
            throw new Exception($"[x] 云端残留 {left} 条 {roomName} 租约未回收");
        Console.WriteLine("[6] 收尾校验   : 云端已无残留租约 ✔");

        // 7) vanilla 房间装配校验（复用同一 mcDir 共享 libraries，仅补下 vanilla 客户端 jar）
        var vanillaInfo = new RoomInfo("MCVANIL", room.Address, room.RemotePort, "1.21.1", "vanilla",
            new List<ModEntry>(), 3600);
        var vassembled = await new RoomAssembler().AssembleAsync(vanillaInfo, mcDir, assembleProgress);
        var vjson = await File.ReadAllTextAsync(Path.Combine(vassembled.InstanceDir, vassembled.InstanceId + ".json"));
        if (!vjson.Contains("--quickPlayMultiplayer") || !vjson.Contains(room.Address))
            throw new Exception("[x] vanilla 实例 json 未烤入 --quickPlayMultiplayer 直连参数");
        if (vjson.Contains("fabric-loader"))
            throw new Exception("[x] vanilla 实例不应包含 fabric-loader 依赖");
        if (vassembled.Mods != 0)
            throw new Exception("[x] vanilla 实例不应同步模组");
        Console.WriteLine($"[7] vanilla 校验: 实例 {vassembled.InstanceId} 装配通过（无 fabric 依赖、直连参数已烤入、模组 0）");
        Console.WriteLine("== 一键开房 + 加入装配 全链路自测通过 ==");
    }
    finally
    {
        manager.Dispose();
        try { await api.ReleaseAsync(roomName); } catch { /* 网络问题时等 TTL 回收 */ }
    }
}

// 仅 vanilla 房间装配校验（无需服务端/隧道，直接构造 RoomInfo 跑 RoomAssembler）
async Task RunVanillaCheck(string mcDir)
{
    var info = new RoomInfo("MCVANIL", "tunnel.example.com:25001", 25001, "1.21.1", "vanilla",
        new List<ModEntry>(), 3600);
    var progress = new Progress<AssembleProgress>(p => Console.WriteLine($"      {p.Step,-11} {p.Detail}"));
    var r = await new RoomAssembler().AssembleAsync(info, mcDir, progress);
    var json = await File.ReadAllTextAsync(Path.Combine(r.InstanceDir, r.InstanceId + ".json"));
    if (!json.Contains("--quickPlayMultiplayer") || !json.Contains(info.Address))
        throw new Exception("[x] vanilla 实例 json 未烤入 --quickPlayMultiplayer 直连参数");
    if (json.Contains("fabric-loader"))
        throw new Exception("[x] vanilla 实例不应包含 fabric-loader 依赖");
    if (r.Mods != 0)
        throw new Exception("[x] vanilla 实例不应同步模组");
    Console.WriteLine($"[√] vanilla 装配通过: {r.InstanceId} 新依赖 {r.NewLibraries} 个，无 fabric 依赖，直连参数已烤入");
}

// 账号 / 好友链路自测：对着本地 dev 模式的 tunnel-api 跑（无需共享密钥）
async Task RunAuthSelfTest(string baseUrl)
{
    Console.WriteLine($"[*] 账号链路 API: {baseUrl}");
    using var api = new StudioApiClient(new StudioConfig(baseUrl, ""));
    var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var emailA = $"selftest{stamp}a@example.com";
    var emailB = $"selftest{stamp}b@example.com";
    // 自测账号的口令不写死在源码里：默认用环境变量，缺失时才回落到一次性随机串
    var password = Environment.GetEnvironmentVariable("PCL_SELFTEST_PASSWORD");
    if (string.IsNullOrWhiteSpace(password))
        password = "St-" + Guid.NewGuid().ToString("N").Substring(0, 12) + "!aZ";

    // 1) 注册（v50.11.17 起没有邮箱验证码：建号只要邮箱 + 口令）
    var userA = await api.RegisterAsync(emailA, password, "自测A");
    if (userA.Nickname != "自测A") throw new Exception($"[x] 昵称未保存: {userA.Nickname}");
    Console.WriteLine($"[1] register   : {userA.Email} / {userA.Nickname}");

    // 2) 同一邮箱重复注册应被拒（对照：如果这条也成功，说明邮箱唯一性没在服务端判）
    try
    {
        await api.RegisterAsync(emailA, password, "重复号");
        throw new Exception("[x] 重复邮箱竟然注册成功");
    }
    catch (StudioApiException ex) when (ex.StatusCode == 409)
    {
        Console.WriteLine($"[2] 重复邮箱   : 已拒绝（{ex.Message}）✔");
    }

    // 3) 登录（错误密码应 401）
    try
    {
        await api.LoginAsync(emailA, "WrongPassword1");
        throw new Exception("[x] 错误密码竟然登录成功");
    }
    catch (StudioApiException ex) when (ex.StatusCode == 401)
    {
        Console.WriteLine($"[3] 错误密码   : 已拒绝 ✔");
    }

    // 4) 正常登录（同时验证注册时的密码哈希与盐一致）
    var loginA = await api.LoginAsync(emailA, password);
    if (loginA.Nickname != "自测A") throw new Exception("[x] 登录后昵称不一致");
    Console.WriteLine($"[4] login      : token={loginA.Token[..12]}…");

    // 5) 令牌校验
    var me = await api.GetMeAsync(loginA.Token);
    if (me is null || me.Email != emailA) throw new Exception("[x] /v1/me 校验失败");
    Console.WriteLine($"[5] /v1/me    : {me.Email} / {me.Nickname} ✔");

    var bad = await api.GetMeAsync("invalid-token");
    if (bad is not null) throw new Exception("[x] 无效令牌竟然通过校验");
    Console.WriteLine($"[6] 无效令牌   : 已拒绝 ✔");

    // 7) 第二个账号 + 好友链路
    var userB = await api.RegisterAsync(emailB, password, "自测B");
    Console.WriteLine($"[7] 第二账号   : {userB.Email} / {userB.Nickname}");

    await api.FriendAddAsync(loginA.Token, emailB);
    Console.WriteLine($"[8] 添加好友   : ok");

    try
    {
        await api.FriendAddAsync(loginA.Token, emailB);
        throw new Exception("[x] 重复添加竟然成功");
    }
    catch (StudioApiException ex) when (ex.StatusCode == 409)
    {
        Console.WriteLine($"[9] 重复添加  : 已拒绝 ✔");
    }

    var friends = await api.FriendListAsync(loginA.Token);
    if (friends.Count != 1 || friends[0].Nickname != "自测B")
        throw new Exception($"[x] 好友列表不符合预期: {friends.Count}");
    Console.WriteLine($"[10] 好友列表  : {friends[0].Nickname} ({friends[0].Email}) ✔");

    await api.FriendRemoveAsync(loginA.Token, emailB);
    var empty = await api.FriendListAsync(loginA.Token);
    if (empty.Count != 0) throw new Exception("[x] 删除好友后列表非空");
    Console.WriteLine($"[11] 删除好友  : 列表已清空 ✔");

    Console.WriteLine("== 账号/好友链路自测全部通过 ==");
}

/// <summary>证书判定自测：拿 --tls 给的那张根去连给定的 https 地址，走的是生产同一份
/// ServerTrust.Validate。由 _test_tls_server.py 逐场景驱动（换服务器证书、换信任的根），
/// 所以这里只报告成/败，不在代码里写死期望。</summary>
static async Task<int> RunTlsCheck(string url, string caPath)
{
    X509Certificate2? root = null;
    if (caPath != "-")
    {
        // "-" 表示用内置那张生产根：测试台因此能同时验「锁定生效」与「换了根就拒」
        try
        {
            root = X509CertificateLoader.LoadCertificateFromFile(caPath);
        }
        catch (Exception e)
        {
            Console.WriteLine($"TLS_BAD_INPUT 根证书读不出来: {e.Message}");
            return 2;
        }
    }
    var host = new Uri(url).Host;
    using var handler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            ServerTrust.Validate(host, cert, errors, root)
    };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    try
    {
        var text = (await http.GetStringAsync(url)).Trim();
        Console.WriteLine($"TLS_OK {text}");
        return 0;
    }
    catch (Exception e)
    {
        var inner = e.InnerException ?? e;
        Console.WriteLine($"TLS_FAIL {inner.GetType().Name}: {e.Message} {inner.Message}");
        return 1;
    }
}
