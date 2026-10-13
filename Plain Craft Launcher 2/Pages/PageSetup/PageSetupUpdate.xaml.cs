using System.Windows;
using System.Windows.Input;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageSetupUpdate
{
    /// <summary>热更新通道的最近一次检查结果（null = 还没检查过、也没有可用缓存）。</summary>
    private HotUpdateService.CheckResult? _hot;

    public PageSetupUpdate()
    {
        InitializeComponent();
        Loaded += (_, _) => Init();
    }

    private void Init()
    {
        // PClonline 唯一更新入口：只用 GitHub Releases 一份清单（与引导器同源）。
        // 旧社区更新链路的 UI 与逻辑已整体移除，不再保留分支。
        ModAnimation.AniControlEnabled += 1;
        TextCurrentVersion.Text = "PClonline " + VersionNameFormat(HotUpdateService.CurrentVersion);
        ModAnimation.AniControlEnabled -= 1;
        // 先把上次检查的结果摆出来（启动时后台已经预热过），有结果就不让页面停在"正在检查更新…"上；
        // 缓存旧于 5 分钟才在后台静默刷新，刷新期间玩家看的是上一次的结果
        _hot = HotUpdateService.PeekCached();
        if (_hot is not null) ShowHot(_hot);
        if (_hot is null || HotUpdateService.CacheIsStale()) CheckHotUpdate(_hot is not null);
    }

    private async void CheckHotUpdate(bool keepShown = false)
    {
        BtnCheckAgain.IsEnabled = false;
        if (!keepShown)
        {
            // 没有可显示的结果时才清空画面；静默刷新期间保持原样，
            // 否则"发现新版本"的文字还挂着，"退出并更新"按钮却先消失了
            BtnChangelog.Visibility = Visibility.Collapsed;
            TextCurrentDesc.Text = "正在检查更新…";
        }
        var result = await HotUpdateService.CheckAsync();
        _hot = result;
        BtnCheckAgain.IsEnabled = true;
        ModBase.Log($"[HotUpdate] 检查完成：ok={result.Ok} local={result.LocalVersion} " +
                     $"remote={result.RemoteVersion} hasUpdate={result.HasUpdate}");
        ShowHot(result);
    }

    private void ShowHot(HotUpdateService.CheckResult result)
    {
        if (!result.Ok)
        {
            TextCurrentDesc.Text = result.Message;
            return;
        }
        if (!result.HasUpdate)
        {
            TextCurrentDesc.Text = "已是最新版本 " + VersionNameFormat(result.RemoteVersion);
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
        var pending = _hot is { HasUpdate: true } ? _hot : null;
        var tip = pending is not null
            ? "将退出启动器并自动安装更新 " + VersionNameFormat(pending.RemoteVersion) +
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

    private void BtnChangelog_Click(object sender, MouseButtonEventArgs e)
    {
        // 该按钮在检查到新版本时被复用为"退出并更新"（见 CheckHotUpdate）
        RestartForHotUpdate();
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
        CheckHotUpdate();
    }
}
