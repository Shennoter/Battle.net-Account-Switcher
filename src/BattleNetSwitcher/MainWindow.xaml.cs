using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BattleNetSwitcher.Core;
using Microsoft.Win32;

namespace BattleNetSwitcher;

public partial class MainWindow : Window
{
    private static readonly Geometry LauncherFoundGlyph = Geometry.Parse("M 0,4 L 3,7 L 8,1");
    private static readonly Geometry LauncherMissingGlyph = Geometry.Parse("M 0,0 L 7,7 M 7,0 L 0,7");
    private readonly SessionService sessions;
    private Catalog catalog = new([]);
    private Account? Selected => AccountsList.SelectedItem as Account;
    private Form currentForm;
    private Account? formAccount;
    private IInputElement? previousFocus;
    private bool running;
    private bool rendering;
    private bool ready;

    public MainWindow() : this(new SessionService()) { }

    internal MainWindow(SessionService sessions)
    {
        this.sessions = sessions;
        InitializeComponent();
        LoadAccounts();
    }

    private void LoadAccounts(Guid? selection = null)
    {
        try
        {
            selection ??= Selected?.Id;
            catalog = sessions.Load();
            ready = true;
            rendering = true;
            var items = catalog.Accounts.OrderByDescending(item => item.LastUsed ?? item.SavedAt)
                .Select(item => item with { IsCurrent = !item.NeedsRefresh && item.Id == catalog.LastRestored }).ToArray();
            AccountsList.ItemsSource = items;
            AccountsList.SelectedItem = items.FirstOrDefault(item => item.Id == (selection ?? catalog.LastRestored));
            if (AccountsList.SelectedIndex < 0 && AccountsList.Items.Count > 0)
                AccountsList.SelectedIndex = 0;
            AccountCount.Text = catalog.Accounts.Length.ToString();
            EmptyState.Visibility = catalog.Accounts.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateLauncherStatus(sessions.FindLauncher());
        }
        catch (Exception error) when (IsExpected(error))
        {
            ready = false;
            UpdateLauncherStatus(null, "无法读取账号资料");
            SetFeedback("无法读取账号资料。请检查本地数据文件和访问权限。", false);
        }
        finally { rendering = false; }

        UpdatePreview();
        UpdateControls();
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering)
            return;
        Feedback.Visibility = Visibility.Collapsed;
        UpdatePreview();
        UpdateControls();
        if (SystemParameters.ClientAreaAnimation)
            CardSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase() });
    }

    private void UpdateLauncherStatus(string? launcher, string? message = null)
    {
        var found = launcher is not null;
        LauncherIndicator.Background = (Brush)FindResource(found ? "Blue" : "Error");
        LauncherGlyph.Data = found ? LauncherFoundGlyph : LauncherMissingGlyph;
        LauncherStatus.Text = message ?? (found ? "已找到战网客户端" : "未找到战网客户端 · 请设置战网路径");
        LauncherStatus.ToolTip = launcher;
    }

    private void UpdatePreview()
    {
        var profile = Selected;
        var current = profile?.IsCurrent == true;
        PreviewName.Text = profile?.Name ?? "未选择账号";
        CardRegion.Text = profile?.Monogram ?? "—";
        PreviewRegion.Text = profile?.RegionName ?? "";
        PreviewRegion.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewState.Text = profile is null ? "待选择"
            : profile.NeedsRefresh ? "需更新会话" : current ? "当前登录" : "已保存";
        AppliedSeal.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateControls()
    {
        var available = ready && !running;
        AccountsList.IsEnabled = available;
        AddButton.IsEnabled = available;
        ClientButton.IsEnabled = available;
        LoginOtherButton.IsEnabled = available && catalog.Accounts.Any(account => !account.NeedsRefresh);
        SettingsButton.IsEnabled = available;
        AccountActions.Visibility = Selected is null ? Visibility.Collapsed : Visibility.Visible;
        AccountActions.IsEnabled = available;
        SwitchButton.IsEnabled = available && Selected is not null;
        if (!running)
            SwitchButton.Content = Selected?.NeedsRefresh == true ? "更新会话  →" : "切换并启动  →";
        Fields.IsEnabled = !running;
        ConfirmButton.IsEnabled = !running;
        CancelButton.IsEnabled = !running;
    }

    private void ShowCapture(object sender, RoutedEventArgs e) => OpenForm(Form.Capture);
    private void ShowEdit(object sender, RoutedEventArgs e) => OpenForm(Form.Edit, Selected);
    private void ShowRefresh(object sender, RoutedEventArgs e) => OpenForm(Form.Refresh, Selected);
    private void ShowDelete(object sender, RoutedEventArgs e) => OpenForm(Form.Delete, Selected);
    private void ShowSettings(object sender, RoutedEventArgs e) => OpenForm(Form.Settings);
    private void ShowLoginOther(object sender, RoutedEventArgs e) => OpenForm(Form.LoginOther);

    private void OpenForm(Form form, Account? account = null)
    {
        if (running)
            return;
        currentForm = form;
        formAccount = account;
        previousFocus = Keyboard.FocusedElement;
        var (title, copy, confirm, fields) = form switch
        {
            Form.Capture => ("保存当前登录", "在战网中登录并勾选保持登录，然后从系统托盘退出战网。", "保存会话", true),
            Form.Edit => ("编辑账号", "修改显示名称和启动时使用的地区。", "保存修改", true),
            Form.Refresh => ("更新会话", $"请先在战网重新登录「{account!.Name}」，再从系统托盘退出。继续后将替换该账号的快照。", "更新会话", false),
            Form.Delete => ("移除账号", $"从列表移除「{account!.Name}」并删除它保存的会话。", "移除账号", false),
            Form.LoginOther => ("登录其他账号", "请先保存当前会话。继续后会关闭战网、重置本机战网会话并打开登录页。已保存的账号仍可从列表恢复。", "打开登录页", true),
            _ => ("战网启动程序", $"{LauncherStatus.ToolTip as string ?? "当前未找到启动程序。"}\n请选择 Battle.net Launcher.exe 或 Battle.net.exe。", "选择程序…", false)
        };
        DialogTitle.Text = title;
        DialogCopy.Text = copy;
        ConfirmButton.Content = confirm;
        Fields.Visibility = fields ? Visibility.Visible : Visibility.Collapsed;
        NameLabel.Visibility = NameInput.Visibility = form == Form.LoginOther ? Visibility.Collapsed : Visibility.Visible;
        NameInput.Text = account?.Name ?? "";
        SetRegion(account?.Region ?? (form == Form.LoginOther ? Selected?.Region : null) ?? AccountRegion.China);
        SetDialogError(null);
        Workspace.IsEnabled = false;
        Overlay.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
            Dialog.BeginAnimation(OpacityProperty, new DoubleAnimation(.62, 1, TimeSpan.FromMilliseconds(150)));
        if (form == Form.LoginOther)
        {
            ConfirmButton.Focus();
        }
        else if (fields)
        {
            NameInput.Focus();
            NameInput.SelectAll();
        }
        else
        {
            CancelButton.Focus();
        }
    }

    private void SetRegion(AccountRegion region)
    {
        China.IsChecked = region == AccountRegion.China;
        Americas.IsChecked = region == AccountRegion.Americas;
        Europe.IsChecked = region == AccountRegion.Europe;
        Asia.IsChecked = region == AccountRegion.Asia;
    }

    private AccountRegion GetRegion() => Americas.IsChecked == true ? AccountRegion.Americas
        : Europe.IsChecked == true ? AccountRegion.Europe : Asia.IsChecked == true ? AccountRegion.Asia : AccountRegion.China;

    private async void ConfirmDialog(object sender, RoutedEventArgs e)
    {
        if (running)
            return;
        var name = NameInput.Text;
        var region = GetRegion();
        var account = formAccount;
        switch (currentForm)
        {
            case Form.Capture:
                await Run(async () => (await sessions.Capture(name, region)).Id, "会话已保存。", inDialog: true);
                break;
            case Form.Edit:
                await Run(async () => (await sessions.Rename(account!.Id, name, region)).Id, "账号已更新。", inDialog: true);
                break;
            case Form.Refresh:
                await Run(async () => (await sessions.Capture(account!.Name, account.Region, account.Id)).Id, "会话已更新。", inDialog: true);
                break;
            case Form.Delete:
                await Run(async () => { await sessions.Remove(account!.Id); return null; }, "账号已移除。", inDialog: true);
                break;
            case Form.LoginOther:
                await Run(async () => { await sessions.PrepareLogin(region); return null; },
                    "已启动战网。登录其他账号后，从托盘退出，再保存或更新会话。", inDialog: true);
                break;
            case Form.Settings:
                var picker = new OpenFileDialog
                {
                    Title = "选择战网启动程序",
                    Filter = "战网启动程序|Battle.net Launcher.exe;Battle.net.exe|可执行文件|*.exe",
                    CheckFileExists = true
                };
                if (picker.ShowDialog(this) == true)
                    await Run(async () => { await sessions.SetLauncher(picker.FileName); return null; }, "已更新战网启动程序。", inDialog: true);
                break;
        }
    }

    private async void SwitchAccount(object sender, RoutedEventArgs e)
    {
        if (Selected is not Account account)
            return;
        if (account.NeedsRefresh)
        {
            OpenForm(Form.Refresh, account);
            return;
        }
        await Run(async () => { await sessions.Switch(account.Id); return account.Id; },
            "已启动战网，请在客户端确认当前登录账号。", switching: true);
    }

    private async void StartClient(object sender, RoutedEventArgs e) =>
        await Run(async () => { await sessions.StartClient(); return null; }, "已启动战网。");

    private async Task Run(Func<Task<Guid?>> action, string message, bool inDialog = false, bool switching = false)
    {
        if (running)
            return;
        var returnFocus = inDialog ? previousFocus : Keyboard.FocusedElement;
        running = true;
        UpdateControls();
        Feedback.Visibility = Visibility.Collapsed;
        SetDialogError(null);
        var original = ConfirmButton.Content;
        if (inDialog)
            ConfirmButton.Content = "处理中…";
        if (switching)
            SwitchButton.Content = "正在恢复会话…";

        try
        {
            var result = await Task.Run(action);
            if (inDialog)
                CloseDialog();
            LoadAccounts(result);
            if (ready)
                SetFeedback(message, true);
            if (switching && SystemParameters.ClientAreaAnimation)
            {
                var stamp = new DoubleAnimation(1.28, 1, TimeSpan.FromMilliseconds(235))
                {
                    EasingFunction = new BackEase { Amplitude = .35, EasingMode = EasingMode.EaseOut }
                };
                SealScale.BeginAnimation(ScaleTransform.ScaleXProperty, stamp);
                SealScale.BeginAnimation(ScaleTransform.ScaleYProperty, stamp);
            }
        }
        catch (Exception error) when (IsExpected(error))
        {
            var explanation = error switch
            {
                CryptographicException => "无法解密会话。请使用保存它的 Windows 用户，或重新登录后更新会话。",
                EndOfStreamException => "会话快照不完整。请重新登录后更新会话。",
                UnauthorizedAccessException or System.Security.SecurityException => "无法访问战网数据。请确认战网已退出，且当前 Windows 用户有权限读写数据。",
                JsonException => "无法读取本地数据。请确认账号资料完整。",
                Win32Exception => "Windows 无法启动指定的战网程序。请检查启动路径后重试。",
                IOException io when io.HResult == unchecked((int)0x80070020) => "战网数据文件正在使用。请从托盘退出战网后重试。",
                _ => error.Message
            };
            if (inDialog)
                SetDialogError(explanation);
            else
                SetFeedback(explanation, false);
        }
        finally
        {
            running = false;
            ConfirmButton.Content = original;
            UpdateControls();
            if (inDialog && Overlay.Visibility == Visibility.Visible)
                Keyboard.Focus(NameInput.IsVisible ? NameInput : ConfirmButton);
            else if (Overlay.Visibility == Visibility.Collapsed)
                Keyboard.Focus(returnFocus is UIElement { IsEnabled: true, IsVisible: true } ? returnFocus : AddButton);
        }
    }

    private static bool IsExpected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or CryptographicException or JsonException or Win32Exception or System.Security.SecurityException;

    private void SetFeedback(string message, bool success)
    {
        Feedback.Background = new SolidColorBrush(success ? Color.FromRgb(232, 241, 231) : Color.FromRgb(250, 231, 229));
        FeedbackText.Foreground = (Brush)FindResource(success ? "Success" : "Error");
        FeedbackText.Text = message;
        Feedback.Visibility = Visibility.Visible;
        RaiseLive(FeedbackText);
    }

    private void SetDialogError(string? text)
    {
        DialogError.Text = text ?? "";
        DialogError.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is not null)
            RaiseLive(DialogError);
    }

    private static void RaiseLive(TextBlock block) =>
        UIElementAutomationPeer.CreatePeerForElement(block)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

    private void CloseDialogClick(object sender, RoutedEventArgs e) => CloseDialog();
    private void CloseDialog()
    {
        Overlay.Visibility = Visibility.Collapsed;
        Workspace.IsEnabled = true;
        if (previousFocus is not null)
            Keyboard.Focus(previousFocus);
    }

    private void OverlayKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || running)
            return;
        e.Handled = true;
        CloseDialog();
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleMaximize();
        else if (WindowState == WindowState.Normal)
            DragMove();
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void WindowClosing(object? sender, CancelEventArgs e) => e.Cancel = running;

    private enum Form { Capture, Edit, Refresh, Delete, Settings, LoginOther }
}
