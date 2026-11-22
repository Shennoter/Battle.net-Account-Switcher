using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BattleNetSwitcher;
using BattleNetSwitcher.Core;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/BattleNetSwitcher;component/Theme.xaml")
        });

        var exit = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                if (args.FirstOrDefault() == "--review")
                {
                    using var review = new Fixture();
                    await review.Service.SetLauncher(review.Launcher);
                    review.WriteState("A");
                    await review.Service.Capture("艾泽拉斯 · 主账号", AccountRegion.China);
                    review.WriteState("B");
                    await review.Service.Capture("周末公会", AccountRegion.Asia);
                    var display = new MainWindow(review.Service) { Title = "战网账号切换器（隔离体验）" };
                    var closed = new TaskCompletionSource();
                    display.Closed += (_, _) => closed.SetResult();
                    display.Show();
                    await closed.Task;
                    exit = 0;
                    return;
                }
                using var fixture = new Fixture();
                await VerifyStorage(fixture);
                using var legacy = new Fixture();
                await VerifyLegacy(legacy);
                using var gui = new Fixture();
                await VerifyWindow(gui, args.FirstOrDefault());
                Console.WriteLine("PASS storage, authenticated restore, rollback, and WPF account workflow");
                exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { app.Shutdown(); }
        };
        app.Run();
        return exit;
    }

    private static async Task VerifyStorage(Fixture f)
    {
        await f.Service.SetLauncher(f.Launcher);
        await ExpectFailure(() => f.Service.Capture("空目录", AccountRegion.China));
        await ExpectFailure(() => f.Service.PrepareLogin(AccountRegion.China));
        f.WriteState("A");
        f.ClearSavedSession();
        await ExpectFailure(() => f.Service.Capture("已退出登录", AccountRegion.China));
        Check(f.Service.Load().Accounts.Length == 0, "an empty login state became a saved account");
        f.WriteState("A");
        var a = await f.Service.Capture("晨雾", AccountRegion.China);
        f.WriteState("B");
        var b = await f.Service.Capture("远行", AccountRegion.Europe);
        await ExpectFailure(() => f.Service.Capture("晨雾", AccountRegion.Asia));

        f.FailLaunch = true;
        await ExpectFailure(() => f.Service.Switch(a.Id));
        f.ExpectState("B");
        Check(f.Service.Load().LastRestored is null, "failed switch updated catalog");
        Check(!File.Exists(Path.Combine(f.Data, "switch.json")), "rollback left journal");

        f.FailLaunch = false;
        f.ExpectedOnLaunch = "A";
        await f.Service.Switch(a.Id);
        f.ExpectedOnLaunch = null;
        f.ExpectState("A");
        Check(f.Region == AccountRegion.China && f.Service.Load().LastRestored == a.Id, "region or restored state");
        var reopened = f.NewService();
        Check(reopened.Load().Accounts.Length == 2, "accounts did not persist");

        f.FailLaunch = true;
        await ExpectFailure(() => reopened.PrepareLogin(AccountRegion.China));
        f.FailLaunch = false;
        f.ExpectState("A");
        f.ExpectFreshOnLaunch = true;
        await reopened.PrepareLogin(AccountRegion.Asia);
        f.ExpectFreshOnLaunch = false;
        Check(f.Region == AccountRegion.Asia && reopened.Load().LastRestored is null, "fresh login kept the previous account selected as restored");
        Check(reopened.Load().Accounts.Length == 2, "fresh login discarded saved accounts");
        await reopened.Switch(a.Id);
        f.ExpectState("A");

        var interrupted = Guid.NewGuid();
        var abandoned = f.Local + $".switch-{interrupted:N}.stage";
        Directory.CreateDirectory(abandoned);
        Disk.Write(Path.Combine(f.Data, "switch.json"), new { Id = interrupted });
        await reopened.StartClient();
        Check(!Directory.Exists(abandoned), "restart left interrupted staging data");
        f.ExpectState("A");

        var path = Path.Combine(f.Data, $"{b.Snapshot:N}.session");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = stream.Length - 1;
            var value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 1));
        }
        await ExpectFailure(() => reopened.Switch(b.Id));
        f.ExpectState("A");

        using (File.Open(Path.Combine(f.Roaming, "Battle.net.config"), FileMode.Open, FileAccess.Read, FileShare.None))
            await ExpectFailure(() => reopened.Capture(a.Name, a.Region, a.Id));
        Check(reopened.Load().Accounts.Single(item => item.Id == a.Id).Snapshot == a.Snapshot, "failed update discarded prior snapshot");

        f.WriteState("C");
        var c = await reopened.Capture(a.Name, a.Region, a.Id);
        Check(c.Snapshot != a.Snapshot, "update kept the old snapshot");
        await reopened.Switch(c.Id);
        f.ExpectState("C");
        await reopened.Rename(c.Id, "暮光", AccountRegion.Americas);
        await reopened.Remove(b.Id);
        Check(reopened.Load().Accounts.Single().Name == "暮光", "rename or remove did not persist");
    }

    private static async Task VerifyLegacy(Fixture f)
    {
        f.WriteState("A");
        Directory.CreateDirectory(f.Data);
        var id = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        var archive = Path.Combine(f.Data, $"{snapshot:N}.session");
        using (var output = new BinaryWriter(File.Create(archive)))
            output.Write(0x31534E42);
        Disk.Write(Path.Combine(f.Data, "accounts.json"), new
        {
            Launcher = f.Launcher,
            Accounts = new[] { new { Id = id, Name = "旧账号", Region = AccountRegion.China, Snapshot = snapshot, SavedAt = DateTimeOffset.Now } }
        });
        Check(f.Service.Load().Accounts.Single().NeedsRefresh, "previous catalog was not marked for refresh");
        await ExpectFailure(() => f.Service.Switch(id));
        f.ExpectState("A");
        Check(File.Exists(archive) && f.Region is null, "legacy switch changed state before deliberate refresh");

        var savedAuth = JsonSerializer.SerializeToUtf8Bytes(RegistryBranch.Read(f.AuthPath));
        Disk.Write(Path.Combine(f.Data, "switch.json"), new
        {
            Id = Guid.NewGuid(), Original = new[] { true, true },
            Registry = ProtectedData.Protect(savedAuth, null, DataProtectionScope.CurrentUser)
        });
        f.ClearSavedSession();
        await f.Service.StartClient();
        f.ExpectState("A");

        var window = new MainWindow(f.Service);
        window.Show();
        try
        {
            await Idle();
            Check(Control<TextBlock>(window, "PreviewState").Text == "需更新会话", "legacy account showed a ready state");
            Invoke(window, "SwitchButton");
            Check(Control<TextBlock>(window, "DialogTitle").Text == "更新会话", "legacy action did not open refresh");
            Invoke(window, "ConfirmButton");
            await WaitFor(() => Control<Grid>(window, "Overlay").Visibility == Visibility.Collapsed);
            Check(Control<TextBlock>(window, "PreviewState").Text == "已保存", "successful refresh did not unlock switching");
        }
        finally { window.Close(); }

        var updated = f.Service.Load().Accounts.Single();
        Check(updated.Id == id && !updated.NeedsRefresh && !File.Exists(archive), "refresh did not upgrade the existing account");
        f.WriteState("B");
        f.ExpectedOnLaunch = "A";
        await f.Service.Switch(id);
    }

    private static async Task VerifyWindow(Fixture f, string? evidence)
    {
        await f.Service.SetLauncher(f.Launcher);
        var backup = f.Launcher + ".fixture";
        File.Move(f.Launcher, backup);
        try
        {
            var missing = new MainWindow(f.Service);
            missing.Show();
            try
            {
                var indicator = Control<Border>(missing, "LauncherIndicator");
                Check(Equals(indicator.Background, missing.FindResource("Error")), "missing launcher did not show its error indicator");
                await Capture(missing, evidence, "07-launcher-missing");
            }
            finally { missing.Close(); }
        }
        finally { File.Move(backup, f.Launcher); }

        var window = new MainWindow(f.Service);
        window.Show();
        try
        {
            await Idle();
            var indicator = Control<Border>(window, "LauncherIndicator");
            var frame = Control<Border>(window, "WindowFrame");
            Check(Equals(indicator.Background, window.FindResource("Blue")), "found launcher did not show its indicator");
            Check(frame.CornerRadius.TopLeft > 0, "normal window frame lost its rounded corners");
            await Capture(window, evidence, "01-empty");
            f.WriteState("A");
            f.ClientOpen = true;
            Invoke(window, "AddButton");
            var name = Control<TextBox>(window, "NameInput");
            name.Text = "艾泽拉斯 · 主账号";
            Invoke(window, "ConfirmButton");
            await WaitFor(() => Control<TextBlock>(window, "DialogError").Visibility == Visibility.Visible);
            Check(name.Text == "艾泽拉斯 · 主账号", "save failure cleared input");
            await Capture(window, evidence, "02-save-error");

            f.ClientOpen = false;
            Invoke(window, "ConfirmButton");
            await WaitFor(() => Control<ListBox>(window, "AccountsList").Items.Count == 1);

            f.WriteState("B");
            Invoke(window, "AddButton");
            name.Text = "周末公会";
            await ReselectRegion(window, "Asia");
            Invoke(window, "ConfirmButton");
            await WaitFor(() => Control<ListBox>(window, "AccountsList").Items.Count == 2);

            f.WriteState("C");
            Invoke(window, "AddButton");
            name.Text = "欧洲旅店";
            await ReselectRegion(window, "Europe");
            Invoke(window, "ConfirmButton");
            var list = Control<ListBox>(window, "AccountsList");
            await WaitFor(() => list.Items.Count == 3);
            Select(window, "艾泽拉斯 · 主账号");
            await Idle();
            CheckRowsVisible(list);

            await Capture(window, evidence, "03-selection");
            Invoke(window, "SwitchButton");
            await WaitFor(() => Control<TextBlock>(window, "FeedbackText").Text.Contains("已启动战网"));
            f.ExpectState("A");
            await Capture(window, evidence, "04-restored");
            await Capture(window, evidence, "05-restored-200", 2);
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await Capture(window, evidence, "06-minimum");
            await Idle();
            CheckRowsVisible(list);
            Check(Control<TextBlock>(window, "PreviewState").Text == "当前登录", "restored account did not show as current");
            Select(window, "周末公会");
            Check(list.Items.Cast<Account>().Single(item => item.IsCurrent).Name == "艾泽拉斯 · 主账号", "selection moved the current account mark");
            Check(Control<TextBlock>(window, "PreviewState").Text == "已保存", "another account showed as current");
            Select(window, "艾泽拉斯 · 主账号");

            Invoke(window, "EditButton");
            name.Text = "艾泽拉斯";
            Invoke(window, "ConfirmButton");
            await WaitFor(() => f.Service.Load().Accounts.Any(item => item.Name == "艾泽拉斯") &&
                Control<Grid>(window, "Overlay").Visibility == Visibility.Collapsed);

            var maximize = Control<Button>(window, "MaximizeButton");
            var glyph = (System.Windows.Shapes.Path)maximize.Content;
            var original = glyph.Data;
            Invoke(window, "MaximizeButton");
            await WaitFor(() => window.WindowState == WindowState.Maximized && Equals(maximize.ToolTip, "还原窗口"));
            Check(glyph.Data != original, "the maximize icon did not change to restore");
            Invoke(window, "MaximizeButton");
            await WaitFor(() => window.WindowState == WindowState.Normal && Equals(maximize.ToolTip, "最大化"));
            Invoke(window, "MinimizeButton");
            await WaitFor(() => window.WindowState == WindowState.Minimized);
        }
        finally { window.Close(); }

        var reopened = new MainWindow(f.NewService());
        var closed = false;
        reopened.Closed += (_, _) => closed = true;
        reopened.Show();
        try
        {
            await Idle();
            Check(Control<TextBlock>(reopened, "PreviewName").Text == "艾泽拉斯", "reopen did not select restored account");
            Invoke(reopened, "DeleteButton");
            Invoke(reopened, "ConfirmButton");
            await WaitFor(() => Control<ListBox>(reopened, "AccountsList").Items.Count == 2);
            Invoke(reopened, "CloseButton");
            await WaitFor(() => closed);
        }
        finally { if (!closed) reopened.Close(); }
    }

    private static T Control<T>(MainWindow window, string name) where T : class =>
        (T)(window.FindName(name) ?? throw new InvalidOperationException(name));

    private static void Invoke(MainWindow window, string button)
    {
        var peer = new ButtonAutomationPeer(Control<Button>(window, button));
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        // UI Automation posts the click to the dispatcher.
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static async Task ReselectRegion(MainWindow window, string region)
    {
        var peer = new RadioButtonAutomationPeer(Control<RadioButton>(window, region));
        ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).Select();
        await Idle();
    }

    private static void Select(MainWindow window, string name)
    {
        var list = Control<ListBox>(window, "AccountsList");
        list.SelectedItem = list.Items.Cast<Account>().Single(item => item.Name == name);
    }

    private static void CheckRowsVisible(ListBox list)
    {
        list.UpdateLayout();
        for (var index = 0; index < list.Items.Count; index++)
        {
            var row = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromIndex(index);
            if (row is null)
                throw new InvalidOperationException("a short account list hid a row");
            var y = row.TranslatePoint(new Point(), list).Y;
            Check(y >= 0 && y + row.ActualHeight <= list.ActualHeight + 1, "selecting an account scrolled a short list");
        }
    }

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private static async Task Capture(MainWindow window, string? evidence, string name, int scale = 1)
    {
        if (evidence is null)
            return;
        await Idle();
        var content = (FrameworkElement)window.Content;
        window.UpdateLayout();
        await Idle();
        await WaitFor(() => Math.Abs(Control<Border>(window, "Dialog").Opacity - 1) < .01 &&
            Math.Abs(Control<TranslateTransform>(window, "CardSlide").Y) < .05 &&
            Math.Abs(Control<ScaleTransform>(window, "SealScale").ScaleX - 1) < .01);
        var width = (int)content.ActualWidth;
        var height = (int)content.ActualHeight;
        Check(width >= 870 && height >= 630, "window did not render at requested size");
        var bitmap = new RenderTargetBitmap(width * scale, height * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, width, height);
            var brush = new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = rect };
            drawing.DrawRectangle(brush, null, rect);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(evidence);
        using var output = File.Create(Path.Combine(evidence, name + ".png"));
        encoder.Save(output);
    }

    private static async Task ExpectFailure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected operation to fail");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"bnet-smoke-{Guid.NewGuid():N}");
        private readonly string key = $@"Software\BattleNetSwitcher.Smoke\{Guid.NewGuid():N}";
        public string Data => Path.Combine(root, "data");
        public string Local => Path.Combine(root, "Local", "Battle.net");
        public string Roaming => Path.Combine(root, "Roaming", "Battle.net");
        public string Launcher => Path.Combine(root, "Battle.net.exe");
        public string AuthPath => $@"{key}\{RegistryBranch.AuthKey}";
        public bool FailLaunch { get; set; }
        public bool ClientOpen { get; set; }
        public bool ExpectFreshOnLaunch { get; set; }
        public string? ExpectedOnLaunch { get; set; }
        public AccountRegion? Region { get; private set; }
        public SessionService Service { get; }

        public Fixture()
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Launcher, "launcher fixture");
            Service = NewService();
        }

        public SessionService NewService() => new(Data, Local, Roaming, key,
            _ => ClientOpen ? Task.FromException(new InvalidOperationException("请从 Windows 托盘彻底退出战网，再重试。")) : Task.CompletedTask,
            (_, region) =>
            {
                if (FailLaunch)
                    throw new InvalidOperationException("Launcher rejected start");
                if (ExpectedOnLaunch is string marker)
                    ExpectState(marker);
                if (ExpectFreshOnLaunch)
                {
                    Check(!Directory.Exists(Local) && !Directory.Exists(Roaming), "fresh launch reused the previous profile directories");
                    Check(RegistryBranch.Read(AuthPath) is null, "fresh launch reused the previous saved login state");
                }
                Region = region;
            });

        public void WriteState(string marker)
        {
            var cache = Path.Combine(Local, "BrowserCaches");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(Roaming);
            File.WriteAllText(Path.Combine(cache, "state"), marker);
            File.WriteAllText(Path.Combine(Roaming, "Battle.net.config"), marker);
            using var registry = Registry.CurrentUser.CreateSubKey(key);
            registry.SetValue("state", marker);
            using var encryption = registry.CreateSubKey("EncryptionKey");
            encryption.SetValue("bytes", new byte[] { (byte)marker[0] });
            using var identity = registry.CreateSubKey("Identity");
            identity.SetValue("profile", new byte[] { (byte)marker[0] });
            registry.DeleteSubKeyTree(RegistryBranch.AuthKey, false);
            using var auth = registry.CreateSubKey(RegistryBranch.AuthKey);
            auth.SetValue(marker, new byte[] { (byte)marker[0] });
        }

        public void ClearSavedSession() => Registry.CurrentUser.DeleteSubKeyTree(AuthPath, false);

        public void ExpectState(string marker)
        {
            Check(File.ReadAllText(Path.Combine(Local, "BrowserCaches", "state")) == marker, "local directory mismatch");
            Check(File.ReadAllText(Path.Combine(Roaming, "Battle.net.config")) == marker, "roaming directory mismatch");
            using var registry = Registry.CurrentUser.OpenSubKey(key);
            Check(Equals(registry?.GetValue("state"), marker), "registry mismatch");
            using var encryption = registry?.OpenSubKey("EncryptionKey");
            using var identity = registry?.OpenSubKey("Identity");
            using var auth = registry?.OpenSubKey(RegistryBranch.AuthKey);
            var expected = new[] { (byte)marker[0] };
            Check(encryption?.GetValue("bytes") is byte[] keyBytes && keyBytes.SequenceEqual(expected), "encryption setup mismatch");
            Check(identity?.GetValue("profile") is byte[] identityBytes && identityBytes.SequenceEqual(expected), "identity mismatch");
            Check(auth?.GetValue(marker) is byte[] authBytes && authBytes.SequenceEqual(expected) && auth.ValueCount == 1, "login data mismatch or stale entry");
        }

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, false);
            Directory.Delete(root, recursive: true);
        }
    }
}
