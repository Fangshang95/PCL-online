using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;

namespace PCL;

public static class UpdateManager
{
    /// <summary>
    ///     MCStudio 定制版标记：启动器自身的更新只走 HotUpdateService + 引导器
    ///     （清单 = GitHub Releases 优先、自建服务器兜底），与社区更新源完全无关。
    ///     旧社区更新链路（Mirror酱 / Pysio / Naids / CE GitHub 源）已于 v50.10.2 整体移除。
    /// </summary>
    public const bool IsMcStudioBuild = true;

    /// <summary>旧链路遗留字段，现已无写入方，恒为 false，仅为兼容外部引用保留。</summary>
    public static bool isUpdateWaitingRestart;

    /// <summary>
    ///     仅用于"导出整合包时内嵌 PCL"：从社区源取最新正式版 PCL CE 给玩家打包用，
    ///     与启动器自身更新无关。
    /// </summary>
    internal static readonly UpdatesWrapperModel packExportServer = new(new List<IUpdateSource>
    {
        new UpdatesMinioModel("https://github.com/PCL-Community/PCL2_CE_Server/raw/main/", "GitHub"),
        new UpdatesMinioModel("https://s3.pysio.online/pcl2-ce/", "Pysio"),
        new UpdatesMinioModel("https://staticassets.naids.com/resources/pclce/", "Naids")
    });

    /// <summary>
    ///     旧链路遗留入口：社区源已移除，PClonline 的版本状态一律由 HotUpdateService 判断。
    ///     这里恒返回 Unknown，避免调用方误判"不是最新版"。
    /// </summary>
    public static UpdateEnums.VersionStatus GetVersionStatus()
    {
        return UpdateEnums.VersionStatus.Unknown;
    }

    /// <summary>生命周期占位：社区公告与自动检查已随旧链路移除，此加载器立即完成。</summary>
    public static ModLoader.LoaderTask<int, int> serverLoader =
        new(Lang.Text("Update.Service.PclCe"), _ => { }, priority: ThreadPriority.BelowNormal);

    /// <summary>
    ///     确保 PathTemp 下的 Latest.exe 是最新正式版的 PCL，它会被用于整合包打包。
    ///     如果不是，则下载一个。（与启动器自身更新无关）
    /// </summary>
    internal static void DownloadLatestPCL(ModLoader.LoaderBase loaderToSyncProgress = null)
    {
        // 注意：导出整合包内嵌的是社区正式版 PCL CE，与 PClonline 自身的更新互不相干
        var latestPCLPath = Path.Combine(ModBase.pathTemp, "CE-Latest.exe");
        var target = packExportServer.GetLatestVersion(UpdateChannel.stable,
            SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64);
        if (target is null)
            throw new Exception(Lang.Text("Update.Error.UnableToGetUpdate"));
        if (File.Exists(latestPCLPath) && (ModBase.GetFileSHA256(latestPCLPath) ?? "") == (target.Sha256 ?? ""))
        {
            ModBase.Log("[System] 最新版 PCL 已存在，跳过下载");
            return;
        }

        if ((ModBase.GetFileSHA256(Basics.ExecutablePath) ?? "") == (target.Sha256 ?? ""))
        {
            ModBase.CopyFile(Basics.ExecutablePath, latestPCLPath);
            return;
        }

        var loaders = packExportServer.GetDownloadLoader(UpdateChannel.stable,
            SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, latestPCLPath);
        var loader = new ModLoader.LoaderCombo<int>(Lang.Text("Update.Task.DownloadLatestStable"), loaders);
        loader.Start();
        loader.WaitForExit();
    }
}
