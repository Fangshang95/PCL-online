using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Win32;
using PCL.Core.App.IoC;
using PCL.Core.IO.Net;
using PCL.Core.IO.Net.Dns;
using PCL.Core.Logging;
using PCL.Core.Utils.OS;
using STUN.Client;
using Sentry;
using Sentry.Extensibility;

namespace PCL.Core.App.Essentials;

[LifecycleScope("telemetry", "遥测")]
[LifecycleService(LifecycleState.Running)]
public sealed partial class TelemetryService
{
    private static void _InitSentry()
    {
        Context.Info("开始初始化 Sentry SDK");
        var dsn = EnvironmentInterop.GetSecret("SENTRY_DSN");
        if (dsn is null)
        {
            Context.Warn("未找到 Sentry DSN");
            return;
        }
        
        var release = $"{Basics.VersionName}";
        
#if DEBUG
        var environment = "Debug";
#else
        var environment = "Production";
#endif
        
        SentrySdk.Init(options =>
        {
            options.Dsn = dsn;
            #if DEBUG
            options.Debug = true;
            #else
            options.Debug = false;
            #endif
            options.SendDefaultPii = false;
            options.IsGlobalModeEnabled = true;
            options.AutoSessionTracking = true;
            options.Release = release;
            options.Environment = environment;
            
            // 应该被直接丢弃不上报的异常类型
            options.AddExceptionFilterForType<TimeoutException>();
            options.AddExceptionFilterForType<HttpRequestException>();
            options.AddExceptionFilterForType<WebException>();
            options.AddExceptionFilterForType<TaskCanceledException>();
            options.AddExceptionFilterForType<DirectoryNotFoundException>();
            options.AddExceptionFilterForType<UnauthorizedAccessException>();
            options.AddExceptionFilterForType<FileNotFoundException>();
            
            // 细分类型的过滤器
            options.AddExceptionFilter(new SocketExceptionFilter());
            
            options.SetBeforeSend(@event => @event.Level is SentryLevel.Debug ? null : @event);
        });
        
        SentrySdk.ConfigureScope(scope =>
        {
            scope.User = new SentryUser
            {
                Id = Utils.Secret.Identify.LauncherId
            };
        });
        
        Context.Info("Sentry SDK 初始化完成");
    }

    // 错误上报
    public static void ReportException(Exception ex, string plain, LogLevel level)
    {
        // MCStudio 定制版：遥测（Sentry）已整体停用，异常只留在本地日志
    }

    // 设备环境上报
    private static void _ReportDeviceEnvironment(TelemetryDeviceEnvironment content)
    {
        Context.Info("正在上报设备环境调查数据");
        
        SentrySdk.ConfigureScope(scope =>
        {
            scope.Contexts["Telemetry"] = content;
        });

        try
        {
            SentrySdk.CaptureMessage("设备环境调查");
            Context.Info("已发送设备环境调查数据");
        }
        catch(Exception ex)
        {
            Context.Error("设备环境调查数据发送失败，请检查网络连接以及使用的版本", ex);
        }
    }

    // ReSharper disable UnusedAutoPropertyAccessor.Local

    private class TelemetryDeviceEnvironment
    {
        public required string Tag { get; set; }
        public required string Id { get; set; }
        [JsonPropertyName("OS")] public required int Os { get; set; }
        public required bool Is64Bit { get; set; }
        [JsonPropertyName("IsARM64")] public required bool IsArm64 { get; set; }
        public required string Launcher { get; set; }
        public required string LauncherBranch {get; set; }
        [JsonPropertyName("UsedOfficialPCL")] public required bool UsedOfficialPcl { get; set; }
        [JsonPropertyName("UsedHMCL")] public required bool UsedHmcl { get; set; }
        [JsonPropertyName("UsedBakaXL")] public required bool UsedBakaXl { get; set; }
        public required ulong Memory { get; set; }
        public required string? NatMapBehaviour { get; set; }
        public required string? NatFilterBehaviour { get; set; }
        [JsonPropertyName("IPv6Status")] public required string Ipv6Status { get; set; }
    }

    // ReSharper disable once InconsistentNaming
    private const string STUN_SERVER_ADDR = "stun.miwifi.com";

    // ReSharper restore UnusedAutoPropertyAccessor.Local
    [LifecycleStart]
    private static async Task _StartAsync()
    {
        // MCStudio 定制版：设备环境调查 / Sentry 错误上报 / STUN 探测全部停用，
        // 服务保持注册（生命周期占位）但不再产生任何网络活动
        await Task.CompletedTask.ConfigureAwait(false);
    }
    
    // 用来细分过滤 SocketException 的过滤器，我觉得应该除了遥测服务之外没有其他东西会用到这破玩意儿
    private sealed class SocketExceptionFilter : IExceptionFilter
    {
        public bool Filter(Exception ex)
        {
            if (ex is SocketException socketEx)
            {
                return socketEx.SocketErrorCode is
                    SocketError.ConnectionRefused or 
                    SocketError.TimedOut or
                    SocketError.HostNotFound or
                    SocketError.NetworkUnreachable or
                    SocketError.ConnectionReset;
            }
            return false;
        }
    }
    
    [LifecycleStop]
    private static void _StopAsync()
    {
        // MCStudio 定制版：Sentry 未初始化，无需清理
    }
}