using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.Utils;
using PCL.Core.App.Localization;
using PCL.Core.Utils.OS;

namespace PCL;

public partial class PageSetupUpdate
{
    public VersionDataModel updateInfo;

    /// <summary>热更新通道的最近一次检查结果（MCStudio 定制版专用）。</summary>
    private HotUpdateService.CheckResult _hot;

    public PageSetupUpdate()
    {
        InitializeComponent();
        Loaded += (_, _) => Init();
    }

    private void Init()
    {
        // MCStudio 定制版：不连社区更新源（Mirror酱/Pysio/Naids/GitHub 一概不请求），
        // 这个页面只展示并驱动 v50.10 热更新链路——与引导器共用同一份清单
        if (UpdateManager.IsMcStudioBuild)
        {
            InitHotUpdate();
            return;
        }

        ModAnimation.AniControlEnabled += 1;
        TextMirrorCDK.Password = Config.Update.MirrorChyanKey;

        ComboSystemUpdateChannel.SelectedIndex = (int)Config.Update.UpdateChannel;
        ComboSystemUpdateMode.SelectedIndex = (int)Config.Update.UpdateMode;

        TextCurrentVersion.Text = "PCL CE " + VersionNameFormat(ModBase.versionBaseName);
        ModAnimation.AniControlEnabled -= 1;
        CheckUpdate();
    }

    #region 热更新（MCStudio 定制版）

    private void InitHotUpdate()
    {
        ModAnimation.AniControlEnabled += 1;
        CardSettings.Visibility = Visibility.Collapsed;
        CardUpdate.Visibility = Visibility.Collapsed;
        CardOtherOption.Visibility = Visibility.Collapsed;
        CardCheck.Visibility = Visibility.Visible;
        TextCurrentVersion.Text = "PClonline " + VersionNameFormat(HotUpdateService.CurrentVersion);
        ModAnimation.AniControlEnabled -= 1;
        CheckHotUpdate();
    }

    private async void CheckHotUpdate()
    {
        BtnCheckAgain.IsEnabled = false;
        BtnChangelog.Visibility = Visibility.Collapsed;
        TextCurrentDesc.Text = "正在检查更新…";
        var result = await HotUpdateService.CheckAsync();
        _hot = result;
        BtnCheckAgain.IsEnabled = true;
        ModBase.Log($"[HotUpdate] 检查完成：ok={result.Ok} local={result.LocalVersion} " +
                     $"remote={result.RemoteVersion} hasUpdate={result.HasUpdate}");
        if (!result.Ok)
        {
            TextCurrentDesc.Text = result.Message;
            return;
        }
        if (!result.HasUpdate)
        {
            TextCurrentDesc.Text = "已是最新版本（更新通道：" + result.RemoteVersion + "）";
            return;
        }

        var size = result.DownloadSize / 1048576.0;
        TextCurrentDesc.Text = "发现新版本 " + VersionNameFormat(result.RemoteVersion) +
                               "，退出启动器即自动" +
                               (result.Incremental ? $"增量更新（约 {size:0.#} MB）" : $"全量更新（约 {size:0.#} MB）");
        BtnChangelog.Text = "退出并更新";
        BtnChangelog.Visibility = Visibility.Visible;
    }

    private void RestartForHotUpdate()
    {
        var hasUpdate = _hot is { HasUpdate: true };
        var tip = hasUpdate
            ? "将退出启动器并自动安装更新 " + VersionNameFormat(_hot.RemoteVersion) +
              "，完成后会自动重新打开。\n若未自动重启，重新双击启动器即可。"
            : "将退出启动器并自动检查安装更新，完成后会自动重新打开。";
        if (ModMain.MyMsgBox(tip, "退出并更新", "退出并更新", "取消") == 2) return;
        if (!HotUpdateService.RequestRestartToUpdate())
        {
            HintService.Hint("写入更新标记失败，请手动重启启动器完成更新", HintType.Error);
            return;
        }
        ModMain.frmMain.EndProgram(false, true);
    }

    #endregion

    private async Task<UpdateStatus> IsLatestAsync()
    {        try
        {
            // 修复：使用 dynamic 绕过命名空间重名导致的编译期类型冲突，
            // 或者你可以尝试替换为 PCL.Core.App.SemVer.Parse(ModBase.versionBaseName)
            if (await UpdateManager.remoteServer.IsLatestAsync(
                    UpdateManager.IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64,
                    SemVer.Parse(ModBase.versionBaseName),
                    ModBase.versionCode))
            {
                ModBase.Log("[Update] 已是最新版本");
                return UpdateStatus.Latest;
            }

            ModBase.Log("[Update] 有可用的新版本");
            return UpdateStatus.Available;
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Setup.Update.Error.NetworkFailed"),
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Setup.Update.Error.NetworkFailed"));
            return UpdateStatus.Error;
        }
    }

    public async void CheckUpdate()
    {
        ModBase.Log("[Update] 开始检查更新");
        CardUpdate.Visibility = Visibility.Collapsed;
        CardCheck.Visibility = Visibility.Visible;
        TextCurrentDesc.Text = Lang.Text("Setup.Update.Checking");
        BtnCheckAgain.IsEnabled = false;
        switch (await IsLatestAsync())
        {
            case UpdateStatus.Available:
            {
                Exception checkUpdateEx = null;
                try
                {
                    updateInfo = UpdateManager.remoteServer.GetLatestVersion(
                        UpdateManager.IsCurrentVersionBeta
                            ? UpdateChannel.beta
                            : UpdateChannel.stable, SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64);
                    TextUpdateName.Text = "PCL CE " + VersionNameFormat(updateInfo.VersionName);
                    var summary = updateInfo.Changelog.Between("<summary>", "</summary>");
                    if (!updateInfo.Changelog.Contains("<summary>") || string.IsNullOrWhiteSpace(summary.Trim()))
                        TextChangelog.Text = Lang.Text("Setup.Update.Changelog.Empty");
                    else
                        TextChangelog.Text = summary;
                }
                catch (Exception ex)
                {
                    checkUpdateEx = ex;
                }

                BtnCheckAgain.IsEnabled = true;
                if (updateInfo is null)
                {
                    TextCurrentDesc.Text = Lang.Text("Setup.Update.CheckFailed");
                    if (checkUpdateEx is not null)
                        ModBase.Log(
                            checkUpdateEx,
                            "[Update] 检查更新失败",
                            ModBase.LogLevel.Msgbox,
                            userSummary: Lang.Text("Update.Check.Failed"));
                    else
                        ModBase.Log(
                            "[Update] 检查更新失败",
                            ModBase.LogLevel.Msgbox,
                            userSummary: Lang.Text("Update.Check.Failed"));
                    return;
                }

                if (UpdateManager.updateLoader is not null && UpdateManager.updateLoader.State == ModBase.LoadState.Loading)
                {
                    BtnUpdate_Timer();
                    BtnUpdate.IsEnabled = false;
                }
                else if (UpdateManager.isUpdateWaitingRestart)
                {
                    BtnUpdate.Text = Lang.Text("Setup.Update.RestartInstall");
                    BtnUpdate.IsEnabled = true;
                }
                else
                {
                    BtnUpdate.Text = Lang.Text("Setup.Update.Install");
                    BtnUpdate.IsEnabled = true;
                }

                CardUpdate.Visibility = Visibility.Visible;
                CardCheck.Visibility = Visibility.Collapsed;
                break;
            }
            case UpdateStatus.Latest:
            {
                CardUpdate.Visibility = Visibility.Collapsed;
                CardCheck.Visibility = Visibility.Visible;
                BtnCheckAgain.IsEnabled = true;
                TextCurrentDesc.Text = Lang.Text("Setup.Update.Latest");
                break;
            }
            case UpdateStatus.Error:
            {
                CardUpdate.Visibility = Visibility.Collapsed;
                CardCheck.Visibility = Visibility.Visible;
                BtnCheckAgain.IsEnabled = true;
                TextCurrentDesc.Text = Lang.Text("Setup.Update.CheckFailed");
                break;
            }
        }
    }

    public void BtnUpdate_Timer()
    {
        while (UpdateManager.updateLoader is not null && UpdateManager.updateLoader.State == ModBase.LoadState.Loading)
        {
            ModBase.RunInUi(() => BtnUpdate.Text = Lang.Number(UpdateManager.updateLoader.Progress, "P2"));
            Thread.Sleep(200);
        }
    }

    private void BtnUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        if (UpdateManager.isUpdateWaitingRestart) UpdateManager.UpdateRestart(true);
        // 开始更新流程
        UpdateManager.UpdateStart(UpdateEnums.UpdateType.UpdateNow);
    }

    private void BtnChangelogDetail_Click(object sender, EventArgs e)
    {
        if (updateInfo is null)
            ModMain.MyMsgBox(Lang.Text("Setup.Update.Changelog.Unavailable"), Lang.Text("Setup.Update.Changelog.Title"));
        else
            ModMain.MyMsgBoxMarkdown(updateInfo.Changelog, Lang.Text("Setup.Update.Changelog.Title"));
    }

    private void ComboSystemUpdateMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled == 0)
            Config.Update.UpdateMode = (LauncherAutoUpdateBehavior)ComboSystemUpdateMode.SelectedIndex;
    }

    private void ComboSystemUpdateBranch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0)
            return;

        var isCancelled = false;
        switch (ComboSystemUpdateChannel.SelectedIndex)
        {
            case 0:
            {
                break;
            }
            case 1:
            {
                if (ModMain.MyMsgBox(Lang.Text("Setup.Update.Channel.Beta.Warning.Message"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Title"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Confirm"),
                        Lang.Text("Common.Action.Cancel"), isWarn: true) == 2)
                    isCancelled = true;
                else
                    CheckUpdate();
                break;
            }
            case 2:
            {
                if (ModMain.MyMsgBox(Lang.Text("Setup.Update.Channel.Dev.Warning.Message"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Title"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Confirm"),
                        Lang.Text("Common.Action.Cancel"), isWarn: true) == 2)
                {
                    isCancelled = true;
                    break;
                }

                var confirmText = Lang.Text("Setup.Update.Channel.Dev.FinalConfirm.ExpectedInput");
                var ret = ModMain.MyMsgBoxInput(
                    Lang.Text("Setup.Update.Channel.Dev.FinalConfirm.Title"),
                    Lang.Text("Setup.Update.Channel.Dev.FinalConfirm.Message", confirmText),
                    button1: Lang.Text("Setup.Update.Channel.Dev.FinalConfirm.Submit"),
                    button2: Lang.Text("Common.Action.Cancel"), isWarn: true);
    
                if (ret == confirmText)
                {
                    CheckUpdate();
                }
                else
                {
                    HintService.Hint(Lang.Text("Setup.Update.Channel.Dev.FinalConfirm.WrongInput"));
                    isCancelled = true;
                }
                break;
            }
        }

        if (isCancelled)
        {
            ModAnimation.AniControlEnabled += 1;
            ComboSystemUpdateChannel.SelectedItem = e.RemovedItems[0];
            ModAnimation.AniControlEnabled -= 1;
        }
        else
        {
            Config.Update.UpdateChannel = (Core.App.UpdateChannel)ComboSystemUpdateChannel.SelectedIndex;
        }
    }

    private void TextMirrorCDK_PasswordChanged(object sender, EventArgs e)
    {
        Config.Update.MirrorChyanKey = TextMirrorCDK.Password;
    }

    private void BtnGetMirrorCDK_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.OpenWebsite("https://mirrorchyan.com/");
    }

    private void BtnChangelog_Click(object sender, MouseButtonEventArgs e)
    {
        // MCStudio 定制版：这个按钮被复用为"退出并更新"（见 CheckHotUpdate）
        if (UpdateManager.IsMcStudioBuild)
        {
            RestartForHotUpdate();
            return;
        }
        ModBase.OpenWebsite("https://github.com/PCL-Community/PCL2-CE/releases/v" + ModBase.versionBaseName);
    }

    public string VersionNameFormat(string str)
    {
        str = str.Replace("v", "");
        if (!str.Contains("-"))
            return str;
        var add = str.AfterLast("-");
        str = str.BeforeLast("-");
        return $"{str} {add.Replace(".", " ").Replace("beta", "Beta").Replace("rc", "RC")}";
    }

    private void BtnCheckAgain_OnClick(object sender, MouseButtonEventArgs e)
    {
        if (UpdateManager.IsMcStudioBuild)
        {
            CheckHotUpdate();
            return;
        }
        CheckUpdate();
    }

    private enum UpdateStatus
    {
        Checking = 0,
        Available = 1,
        Error = 2,
        Latest = 3
    }
}
