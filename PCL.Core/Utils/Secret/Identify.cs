using System;
using System.Management;
using System.Text;
using PCL.Core.Logging;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.Hash;

namespace PCL.Core.Utils.Secret;

public class Identify
{
    public static byte[] RawId { get => field ??= _GetRawId(); } = null!;
    public static string LauncherId { get => field ??= _getLauncherId(); } = null!;

    private static bool _trusted;

    /// <summary>
    /// 指纹是否可信（至少采到 2 个硬件分量）。不可信时 LauncherId 仍可用于展示与崩溃报告，
    /// 但绝不能用于任何限制性判定（如注册名额闸门）——否则采集失败的真人会被误拒。
    /// </summary>
    public static bool Trusted
    {
        get
        {
            _ = RawId; // 触发一次采集，_GetRawId 内部负责置 _trusted
            return _trusted;
        }
    }

    private static byte[] _GetRawId()
    {
        var code = new StringBuilder();
        var components = 0;
        try
        {
            var uuid = _GetWmiProperty("Win32_ComputerSystemProduct", "UUID");
            var mbProd = _GetWmiProperty("Win32_BaseBoard", "Product");
            var mbSn = _GetWmiProperty("Win32_BaseBoard", "SerialNumber");
            var cpu = _GetWmiProperty("Win32_Processor", "ProcessorId");
            components = (string.IsNullOrWhiteSpace(uuid) ? 0 : 1)
                         + (string.IsNullOrWhiteSpace(mbProd) ? 0 : 1)
                         + (string.IsNullOrWhiteSpace(mbSn) ? 0 : 1)
                         + (string.IsNullOrWhiteSpace(cpu) ? 0 : 1);

            code.Append("UUID:").Append(uuid)
                .Append("|MB_Prod:").Append(mbProd)
                .Append("|MB_SN:").Append(mbSn)
                .Append("|CPU:").Append(cpu);

            // WMI 一项都拿不到时整串会退化成空壳，所有失败机器算出同一个值。
            // 回退到注册表 MachineGuid：不走 WMI、标准用户可读、只在重装系统时变化。
            // 只在降级路径追加，保证正常机器的指纹输入与历史版本逐字节一致。
            if (components < 2)
            {
                var machineGuid = _GetMachineGuid();
                code.Append("|MGUID:").Append(machineGuid);
                if (!string.IsNullOrWhiteSpace(machineGuid)) components++;
            }
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Identify", "获取设备基础信息失败");
        }

        _trusted = components >= 2;
        return Encoding.UTF8.GetBytes(SHA512Provider.Instance.ComputeHash(code.ToString()).ToHexString());
    }

    private static string _GetMachineGuid()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString()?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Identify", "读取 MachineGuid 失败");
            return string.Empty;
        }
    }

    private static string _GetWmiProperty(string className, string propertyName)
    {
        try
        {
            using var searcher =
                new ManagementObjectSearcher($"SELECT {propertyName} FROM {className}");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                if (obj[propertyName] is not null)
                    return (obj[propertyName].ToString() ?? string.Empty).Trim();
            }
        }
        catch { /* Ignore */ }
        return string.Empty;
    }

    private static string _getLauncherId()
    {
        try
        {
            var prefix = "PCL-CE|"u8.ToArray();
            var ctx = RawId;
            var suffix = "|LauncherId"u8.ToArray();

            var buffer = new byte[prefix.Length + ctx.Length + suffix.Length];
            var bufferSpan = buffer.AsSpan();
            prefix.CopyTo(bufferSpan[..prefix.Length]);
            ctx.CopyTo(bufferSpan.Slice(prefix.Length, ctx.Length));
            suffix.CopyTo(bufferSpan.Slice(prefix.Length + ctx.Length, suffix.Length));

            Array.Clear(ctx);
            var sample = SHA512Provider.Instance.ComputeHash(bufferSpan).ToHexString();
            bufferSpan.Clear();

            // 16 in length, 8 bytes, 64 bits, enough for us
            return sample.Substring(64, 16)
                .ToUpper()
                .Insert(4, "-")
                .Insert(9, "-")
                .Insert(14, "-");
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Identify", "无法获取识别码");
            return "PCL2-CECE-GOOD-2025";
        }
    }
}