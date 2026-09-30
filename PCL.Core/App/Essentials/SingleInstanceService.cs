using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using PCL.Core.App.IoC;
using PCL.Core.IO;
using PCL.Core.Utils;

namespace PCL.Core.App.Essentials;

[LifecycleService(LifecycleState.BeforeLoading, Priority = -2134567890)]
[LifecycleScope("single-instance", "单例", false)]
public sealed partial class SingleInstanceService
{
    private static FileStream? _lockStream;
    private static readonly string _LockFilePath = Path.Combine(Paths.SharedLocalData, "instance.lock");

    private static void _TryRpc(string processId, string content)
    {
        var pipeName = $"{RpcService.PipePrefix}@{processId}";
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(1000);
        using var sw = new StreamWriter(pipe, PipeComm.PipeEncoding);
        sw.WriteLine(content);
        sw.Write(PipeComm.PipeEndingChar);
        sw.Flush();
    }

    [LifecycleStart]
    private static void _Start()
    {
        try
        {
            var stream = File.Open(_LockFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            Context.Debug("未发现重复实例，正在向单例锁写入信息");
            using var sw = new StreamWriter(stream, Encoding.ASCII, 8, true);
            sw.Write(Basics.CurrentProcessId);
            sw.Flush();
            _lockStream = stream;
        }
        catch (Exception)
        {
            // v50.9.10：锁里记录的实例可能是"僵尸"——进程已死，或进程活着但一直没有主窗口
            // （早期弹窗卡住 UI 线程就会这样）。此时若照旧退出，用户双击将永远打不开启动器。
            // 先体检：不合格就清锁并由本实例接管启动。
            if (_TryTakeOverStaleLock()) return;
            var takenOver = false;
            try
            {
                using var stream = File.Open(_LockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var pid = reader.ReadToEnd();
                Context.Info($"发现重复实例 {pid}，尝试传递参数并拉起主窗口");
                try
                {
                    _TryRpc(pid, "REQ cli\n" + JsonSerializer.Serialize(StartupService.UnhandledCommands, JsonCompat.SerializerOptions));
                    _TryRpc(pid, "REQ activate");
                }
                catch (Exception ex)
                {
                    Context.Warn("RPC 通信失败", ex);
                    // v50.9.11：管道断开多半是对方正在退出（自提权重启的竞态）——再体检一次，
                    // 对方若已消失就接管启动，避免"新旧实例一起退出、桌面上什么都没有"
                    if (_TryTakeOverStaleLock())
                    {
                        Context.Info("已接管（原实例正在退出），本实例继续启动");
                        takenOver = true;
                    }
                }
            }
            catch (Exception ex) { Context.Error("读取单例锁出错", ex); }
            finally { if (!takenOver) Context.RequestExit(1); }
        }
    }

    /// <summary>
    /// v50.9.11：主动释放单例锁（供「以管理员身份重启」使用）。
    /// 自提权时必须**先放锁再拉起新进程**——否则新进程会看到锁还在、判定为重复实例而退出，
    /// 而旧进程随后也退出，两边都没了（用户视角：点了重启后启动器消失）。
    /// </summary>
    public static void ReleaseLock()
    {
        try { _lockStream?.Dispose(); } catch { }
        _lockStream = null;
        try { if (File.Exists(_LockFilePath)) File.Delete(_LockFilePath); } catch { }
    }

    /// <summary>
    /// v50.9.10：体检单例锁里记录的实例——
    /// ① 进程不存在（陈旧锁，如上次被强杀）→ 清锁接管；
    /// ② 进程存活但长时间（&gt;15s）没有主窗口 → 判定僵尸，结束后接管；
    /// ③ 正常实例 → 返回 false，交回原逻辑（RPC 拉起主窗口后本实例退出）。
    /// 返回 true = 本实例已接管锁并继续启动。
    /// </summary>
    private static bool _TryTakeOverStaleLock()
    {
        try
        {
            string text;
            using (var s = File.Open(_LockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(s)) { text = r.ReadToEnd(); }
            if (!int.TryParse(text.Trim(), out var pid) || pid <= 0) return false;

            Process? target = null;
            try { target = Process.GetProcessById(pid); }
            catch (ArgumentException) { target = null; }        // 进程已不存在
            catch (InvalidOperationException) { target = null; }

            if (target is null)
            {
                Context.Info($"单例锁记录的实例 {pid} 已不存在，清理锁后由本实例接管");
            }
            else
            {
                double age;
                try { age = (DateTime.Now - target.StartTime).TotalSeconds; }
                catch { age = 0; }
                if (target.HasExited)
                {
                    Context.Info($"实例 {pid} 已退出但锁未清理，由本实例接管");
                }
                else if (target.MainWindowHandle == IntPtr.Zero && age > 15)
                {
                    Context.Info($"实例 {pid} 存活 {age:F0}s 仍无主窗口（僵尸实例），结束后由本实例接管");
                    try { target.Kill(true); } catch { }
                    Thread.Sleep(300);
                }
                else
                {
                    return false;   // 正常实例：拉起它的主窗口即可
                }
            }

            try { File.Delete(_LockFilePath); } catch { }
            var stream = File.Open(_LockFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            using var sw = new StreamWriter(stream, Encoding.ASCII, 8, true);
            sw.Write(Basics.CurrentProcessId);
            sw.Flush();
            _lockStream = stream;
            Context.Debug("已重新写入单例锁，本实例继续启动");
            return true;
        }
        catch (Exception ex)
        {
            Context.Warn("接管陈旧单例锁失败", ex);
            return false;
        }
    }

    [LifecycleStop]
    private static void _Stop()
    {
        if (_lockStream is null) return;
        Context.Debug("正在删除单例锁");
        _lockStream.Dispose();
        File.Delete(_LockFilePath);
    }
}
