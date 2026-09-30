using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading.Tasks;

namespace PCL;

/// <summary>
/// v50.9.7 防火墙/杀毒防护（原静默失败家族统一收口）。
/// 背景：Windows 防火墙里**阻止规则永远压过放行规则**，玩家机器存在「PClonine 入站阻止」时，
/// 光加放行毫无作用；而 netsh 添加规则需要管理员，非管理员 exit 1 被 catch 吞掉 → 打洞/直连全灭。
/// 策略：
///   ① 管理员下：清理针对本程序的阻止规则 + 建立放行四件套 + Defender 排除项（一次性，写 stamp）
///   ② 非管理员：不静默——提示用户并以管理员身份重启（UAC 自提权，最小权限，只修一次就退出）
///   ③ 火墙命令失败可见化（写日志 + 状态提示），不再 catch{} 吞掉
/// </summary>
public static class StudioFirewall
{
    private const string StampName = "firewall-fixed.stamp";
    private const string StampVersion = "v50.9.7";

    /// <summary>当前进程是否为管理员。</summary>
    public static bool IsAdministrator
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    private static string ExePath => Environment.ProcessPath ?? "";

    private static string StampPath
        => Path.Combine(ModBase.exePath, "PCL", StampName);

    /// <summary>本会话是否已提示过管理员（避免每次点都弹）。</summary>
    private static bool _prompted;

    /// <summary>
    /// 联机/开房入口调用：管理员 → 静默确保规则；非管理员 → 提示并可一键以管理员重启。
    /// 返回是否已具备管理员权限（false 表示用户选择继续，功能可能降级）。
    /// </summary>
    public static bool EnsureAdminInteractive(string action)
    {
        if (IsAdministrator)
        {
            EnsureRules(interactive: true);
            return true;
        }
        if (_prompted) return false;
        _prompted = true;
        var choice = ModBase.RunInUiWait(() => ModMain.MyMsgBox(
            $"「{action}」需要管理员权限才能放行防火墙（打洞直连、局域网联机都依赖入站放行）。\n\n" +
            "当前不是管理员运行，可能导致：朋友连不上（只能走中转，限速 2MB/s）、P2P 直连失败。\n\n" +
            "建议点「以管理员身份重启」（只弹一次 UAC，重启后不再提示）；也可以继续，但联机质量会下降。",
            "建议以管理员身份运行 PClonline",
            "以管理员身份重启", "继续（可能降级）"));
        if (choice == 1)
        {
            ElevateRestart();
            return false;   // 不会走到这里（重启后进程退出）
        }
        return false;
    }

    /// <summary>
    /// v50.9.8 启动入口（主窗体就绪后调用一次）：
    /// 管理员 → 后台静默完成防护，不弹任何窗；
    /// 非管理员 → 弹一次「以管理员身份重启」提示（选继续则本次会话不再打扰）。
    /// 取代原先随包分发的独立 bat——现在只分享 exe 即可。
    /// </summary>
    public static void PromptAdminAtStartup()
    {
        try
        {
            if (IsAdministrator)
            {
                // 静默防护放到后台线程：PowerShell 枚举 + netsh 可能数秒，绝不能卡 UI
                _ = Task.Run(() =>
                {
                    try { EnsureRules(interactive: false); }
                    catch { /* 防护失败不影响使用 */ }
                });
                return;
            }
            if (_prompted) return;
            _prompted = true;
            ShowAdminPromptDialog();
        }
        catch (Exception ex)
        {
            ModBase.Log("[Firewall] 启动管理员提示失败（忽略）：" + ex.Message);
        }
    }

    private static void ShowAdminPromptDialog()
    {
        try
        {
            var choice = ModMain.MyMsgBox(
                "建议以管理员身份运行 PClonline。\n\n" +
                "原因：Windows 防火墙的入站放行需要管理员权限，非管理员运行会导致——\n" +
                "· 朋友连不上你的房间（只能走中转通道，限速 2MB/s）\n" +
                "· P2P 打洞直连失败，延迟变高\n\n" +
                "点「以管理员身份重启」只需弹一次系统确认，之后以管理员启动时不会再提示。",
                "建议以管理员身份运行 PClonline",
                "以管理员身份重启", "继续（联机质量可能下降）");
            if (choice == 1) ElevateRestart(andExit: true);
        }
        catch (Exception ex)
        {
            ModBase.Log("[Firewall] 管理员提示弹窗失败（忽略）：" + ex.Message);
        }
    }

    /// <summary>UAC 自提权重启自身（不带参数 = 正常启动，管理员身份下自动完成防护）。</summary>
    public static void ElevateRestart(bool andExit = false)
    {
        try
        {
            var exe = ExePath;
            if (string.IsNullOrEmpty(exe)) return;
            // v50.9.11：必须先释放单例锁再拉起新进程——否则新进程看到锁还在会判定为
            // 重复实例而退出，而旧进程随后也退出（20:24 的失败链条：两边都没了）
            try { PCL.Core.App.Essentials.SingleInstanceService.ReleaseLock(); }
            catch (Exception ex) { ModBase.Log("[Firewall] 释放单例锁失败：" + ex.Message); }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            ModBase.Log("[Firewall] 已请求以管理员身份重启");
            if (andExit)
            {
                // 让新实例先起来，再结束当前普通权限实例
                _ = Task.Run(async () =>
                {
                    await Task.Delay(800).ConfigureAwait(false);
                    try { Environment.Exit(0); } catch { }
                });
            }
        }
        catch (Exception ex)
        {
            ModBase.Log("[Firewall] 管理员重启失败：" + ex.Message);
            HintService.Hint("以管理员身份重启失败，请手动右键启动器 → 以管理员身份运行", HintType.Warning);
        }
    }

    /// <summary>
    /// 建立/维护放行规则（幂等）。管理员下执行：删阻止规则 → 放行四件套 → Defender 排除 → 写 stamp。
    /// 非管理员只是记录日志（由 EnsureAdminInteractive 决定是否提示）。
    /// </summary>
    public static bool EnsureRules(bool interactive)
    {
        try
        {
            if (File.Exists(StampPath))
            {
                if (File.ReadAllText(StampPath).Trim() == StampVersion) return true;
            }
        }
        catch { }

        if (!IsAdministrator)
        {
            ModBase.Log("[Firewall] 非管理员：跳过防火墙规则配置（放行规则需管理员）");
            if (interactive)
                HintService.Hint("未以管理员运行：防火墙入站放行未配置，联机可能只能走中转",
                    HintType.Warning);
            return false;
        }

        var ok = DeleteBlockRules();
        ok &= AddAllowRules();
        AddDefenderExclusions();   // 失败不影响判定（第三方 AV 接管时会失败）
        if (ok)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StampPath)!);
                File.WriteAllText(StampPath, StampVersion);
            }
            catch { }
            ModBase.Log("[Firewall] 防火墙放行规则已配置（管理员一次性修复完成）");
        }
        return ok;
    }

    /// <summary>删除针对本程序的阻止规则（Block 永远压过 Allow，必须清掉）。</summary>
    private static bool DeleteBlockRules()
    {
        var exe = ExePath.Replace("'", "''");
        // ① 程序级：任何把本 exe 设为阻止的规则
        // ② 名称级：自家历史规则（PClonine/PClonline/MCStudio 前缀）里的阻止项
        var ps =
            "$E='" + exe + "';" +
            "Get-NetFirewallApplicationFilter -Program $E -ErrorAction SilentlyContinue | " +
            "ForEach-Object { Get-NetFirewallRule -AssociatedNetFirewallApplicationFilter $_ " +
            "-ErrorAction SilentlyContinue } | Where-Object { $_.Action -eq 'Block' } | " +
            "ForEach-Object { Remove-NetFirewallRule -Name $_.Name -ErrorAction SilentlyContinue; " +
            "Write-Output ('removed:' + $_.DisplayName) };" +
            "Get-NetFirewallRule -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.DisplayName -match 'PClonine|PClonline|MCStudio' -and $_.Action -eq 'Block' } | " +
            "ForEach-Object { Remove-NetFirewallRule -Name $_.Name -ErrorAction SilentlyContinue; " +
            "Write-Output ('removed:' + $_.DisplayName) }";
        var (code, outp, errp) = RunHidden("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps.Replace("\"", "`\"") + "\"");
        ModBase.Log($"[Firewall] 阻止规则清理 exit={code} {Trim(outp)}{Trim(errp)}");
        return code == 0;
    }

    /// <summary>放行四件套：启动器 UDP/TCP 入站、frpc 出站、Mojang 运行时 java 入站。</summary>
    private static bool AddAllowRules()
    {
        var exe = ExePath;
        var ok = true;
        ok &= Netsh($"advfirewall firewall delete rule name=\"PClonine-In-UDP\"") &&
              Netsh($"advfirewall firewall add rule name=\"PClonine-In-UDP\" dir=in action=allow " +
                    $"program=\"{exe}\" protocol=udp enable=yes profile=any");
        ok &= Netsh($"advfirewall firewall delete rule name=\"PClonine-In-TCP\"") &&
              Netsh($"advfirewall firewall add rule name=\"PClonine-In-TCP\" dir=in action=allow " +
                    $"program=\"{exe}\" protocol=tcp enable=yes profile=any");
        var frpc = Path.Combine(ModBase.exePath, "mcstudio", "frpc", "frpc.exe");
        if (File.Exists(frpc))
            ok &= Netsh($"advfirewall firewall delete rule name=\"PClonine-frpc-Out\"") &&
                  Netsh($"advfirewall firewall add rule name=\"PClonine-frpc-Out\" dir=out action=allow " +
                        $"program=\"{frpc}\" enable=yes profile=any");
        // MC 运行时 java（房主 LAN 世界 / 服务端入站直通）
        foreach (var java in EnumerateJavaRuntimeExes())
        {
            Netsh($"advfirewall firewall delete rule name=\"PClonine-java-{java.GetHashCode():X}\"");
            ok &= Netsh($"advfirewall firewall add rule name=\"PClonine-java-{java.GetHashCode():X}\" " +
                        $"dir=in action=allow program=\"{java}\" protocol=tcp enable=yes profile=any");
        }
        return ok;
    }

    private static IEnumerable<string> EnumerateJavaRuntimeExes()
    {
        var roots = new List<string>();
        try
        {
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            roots.Add(Path.Combine(appdata, ".minecraft", "runtime"));
            roots.Add(Path.Combine(ModBase.exePath, "runtime"));
        }
        catch { }
        var found = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var f in Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories))
                {
                    found.Add(f);
                    if (found.Count >= 8) break;
                }
                foreach (var f in Directory.EnumerateFiles(root, "javaw.exe", SearchOption.AllDirectories))
                {
                    found.Add(f);
                    if (found.Count >= 16) break;
                }
            }
            catch { }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Defender 排除（第三方 AV 接管时 AMRunningMode=Passive，命令会静默失败——容错）。</summary>
    private static void AddDefenderExclusions()
    {
        try
        {
            var dir = Path.Combine(ModBase.exePath, "mcstudio").Replace("'", "''");
            var ps =
                "if ((Get-MpComputerStatus -ErrorAction SilentlyContinue).AMRunningMode -ne 'Normal') { " +
                "Write-Output 'skip:defender-passive'; exit 0 };" +
                "Add-MpPreference -ExclusionPath '" + dir + "' -ErrorAction Stop;" +
                "Add-MpPreference -ExclusionProcess 'frpc.exe' -ErrorAction Stop;" +
                "Write-Output 'ok'";
            var (code, outp, errp) = RunHidden("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps.Replace("\"", "`\"") + "\"");
            ModBase.Log($"[Firewall] Defender 排除 exit={code} out={Trim(outp)} err={Trim(errp)}");
        }
        catch (Exception ex)
        {
            ModBase.Log("[Firewall] Defender 排除失败（忽略）：" + ex.Message);
        }
    }

    private static bool Netsh(string args)
    {
        var (code, _o, e) = RunHidden("netsh", args);
        if (code != 0) ModBase.Log($"[Firewall] netsh 失败({code}): {args} {Trim(e)}");
        return code == 0;
    }

    private static (int Code, string Out, string Err) RunHidden(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return (-1, "", "start failed");
            var o = p.StandardOutput.ReadToEnd();
            var e = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode, o, e);
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }

    private static string Trim(string s)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }
}
