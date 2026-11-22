using System.Security.Principal;
using System.Windows;

namespace BattleNetSwitcher;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        using var identity = WindowsIdentity.GetCurrent();
        instance = new Mutex(false, $@"Global\BattleNetSwitcher-{identity.User!.Value}");
        try { ownsInstance = instance.WaitOne(0); }
        catch (AbandonedMutexException) { ownsInstance = true; }

        if (!ownsInstance)
        {
            MessageBox.Show("战网账号切换器已在运行。", "战网账号切换器");
            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance)
            instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
