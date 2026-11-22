using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace BattleNetSwitcher.Core;

public sealed class SessionService
{
    private readonly string[] roots;
    private readonly string registryPath;
    private readonly string dataFolder;
    private readonly string catalogPath;
    private readonly string journalPath;
    private readonly Func<bool, Task> ensureClosed;
    private readonly Action<string, AccountRegion?> launch;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SessionService() : this(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BattleNetSwitcher"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Battle.net"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Battle.net"),
        @"Software\Blizzard Entertainment\Battle.net", Client.EnsureClosed, Client.Launch) { }

    internal SessionService(string dataFolder, string local, string roaming, string registryPath,
        Func<bool, Task> ensureClosed, Action<string, AccountRegion?> launch)
    {
        this.dataFolder = dataFolder;
        this.registryPath = registryPath;
        this.ensureClosed = ensureClosed;
        this.launch = launch;
        roots = [local, roaming];
        catalogPath = Path.Combine(dataFolder, "accounts.json");
        journalPath = Path.Combine(dataFolder, "switch.json");
    }

    public Catalog Load() => File.Exists(catalogPath) ? Disk.Read<Catalog>(catalogPath) : new Catalog([]);

    public string? FindLauncher() => Client.FindLauncher(Load().Launcher);

    public Task<Account> Capture(string name, AccountRegion region, Guid? replace = null) => Exclusive(async () =>
    {
        await Recover();
        var catalog = Load();
        name = ValidateName(catalog, name, replace);
        var old = replace is Guid id ? Find(catalog, id) : null;
        await ensureClosed(false);
        if (!Disk.Exists(roots[1]) || !File.Exists(Path.Combine(roots[1], "Battle.net.config")))
            throw new InvalidOperationException("找不到战网配置。请先在客户端登录、勾选保持登录，再从托盘退出。");
        var registry = RegistryBranch.Read(registryPath);
        if (registry?.HasSavedSession != true)
            throw new InvalidOperationException("找不到已保存的战网登录记录。请在战网登录并勾选保持登录，再从托盘退出战网。");

        Directory.CreateDirectory(dataFolder);
        var account = new Account(old?.Id ?? Guid.NewGuid(), name, region, Guid.NewGuid(), DateTimeOffset.Now,
            old?.LastUsed, SnapshotArchive.Version);
        var path = SnapshotPath(account.Snapshot);
        try
        {
            SnapshotArchive.Write(path, roots, registry);
            Save(catalog with
            {
                Accounts = old is null ? [..catalog.Accounts, account]
                    : catalog.Accounts.Select(item => item.Id == account.Id ? account : item).ToArray(),
                LastRestored = catalog.LastRestored == account.Id ? null : catalog.LastRestored
            });
        }
        catch
        {
            Disk.TryClean(() => File.Delete(path));
            throw;
        }
        if (old is not null)
            Disk.TryClean(() => File.Delete(SnapshotPath(old.Snapshot)));
        return account;
    });

    public Task<Account> Rename(Guid id, string name, AccountRegion region) => Exclusive(async () =>
    {
        await Recover();
        var catalog = Load();
        var account = Find(catalog, id) with { Name = ValidateName(catalog, name, id), Region = region };
        Save(catalog with { Accounts = catalog.Accounts.Select(item => item.Id == id ? account : item).ToArray() });
        return account;
    });

    public Task Remove(Guid id) => Exclusive(async () =>
    {
        await Recover();
        var catalog = Load();
        var account = Find(catalog, id);
        Save(catalog with
        {
            Accounts = catalog.Accounts.Where(item => item.Id != id).ToArray(),
            LastRestored = catalog.LastRestored == id ? null : catalog.LastRestored
        });
        Disk.TryClean(() => File.Delete(SnapshotPath(account.Snapshot)));
        return true;
    });

    public Task SetLauncher(string path) => Exclusive(async () =>
    {
        await Recover();
        if (!Client.IsLauncher(path))
            throw new InvalidOperationException("请选择 Battle.net Launcher.exe 或 Battle.net.exe。");
        Save(Load() with { Launcher = Path.GetFullPath(path) });
        return true;
    });

    public Task StartClient() => Exclusive(async () =>
    {
        await Recover();
        launch(RequireLauncher(), null);
        return true;
    });

    public Task Switch(Guid id) => Exclusive(async () =>
    {
        await Recover();
        var catalog = Load();
        var account = Find(catalog, id);
        if (account.NeedsRefresh)
            throw new InvalidOperationException(SnapshotArchive.RefreshRequired);
        return await Apply(catalog, RequireLauncher(), account.Region, account);
    });

    public Task PrepareLogin(AccountRegion region) => Exclusive(async () =>
    {
        await Recover();
        var catalog = Load();
        if (!catalog.Accounts.Any(account => !account.NeedsRefresh))
            throw new InvalidOperationException("请先保存当前会话，或更新一个旧版会话。");
        return await Apply(catalog, RequireLauncher(), region, null);
    });

    private async Task<bool> Apply(Catalog catalog, string launcher, AccountRegion region, Account? account)
    {
        var transaction = Guid.NewGuid();
        var areas = WorkAreas(transaction);
        var journal = new Journal(transaction, null, null, null, null, FullRegistry: true);
        Disk.Write(journalPath, journal);
        try
        {
            var registry = account is null ? null : SnapshotArchive.Extract(SnapshotPath(account.Snapshot),
                areas.Select(area => area.Stage).ToArray());
            await ensureClosed(true);
            var previousRegistry = JsonSerializer.SerializeToUtf8Bytes(RegistryBranch.Read(registryPath));
            journal = journal with
            {
                Original = areas.Select(area => Disk.Exists(area.Live)).ToArray(),
                Registry = ProtectedData.Protect(previousRegistry, null, DataProtectionScope.CurrentUser)
            };
            Disk.Write(journalPath, journal);

            foreach (var area in areas)
            {
                if (Disk.Exists(area.Live))
                    Directory.Move(area.Live, area.Backup);
                if (Disk.Exists(area.Stage))
                    Directory.Move(area.Stage, area.Live);
            }

            if (registry is null)
                RegistryBranch.Replace($@"{registryPath}\{RegistryBranch.AuthKey}", null);
            else
                registry.RestoreSession(registryPath);
            launch(launcher, region);

            journal = journal with { Restored = account?.Id, RestoredAt = DateTimeOffset.Now };
            Disk.Write(journalPath, journal);
            Complete(journal, catalog);
        }
        catch
        {
            if (!journal.Committed)
                await Recover();
            throw;
        }
        return true;
    }

    private async Task Recover()
    {
        if (!File.Exists(journalPath))
            return;
        var journal = Disk.Read<Journal>(journalPath);
        var areas = WorkAreas(journal.Id);
        if (journal.Committed)
        {
            Complete(journal, Load());
            return;
        }
        if (journal.Original is null)
        {
            foreach (var area in areas)
                Disk.RemoveDirectory(area.Stage);
            File.Delete(journalPath);
            return;
        }
        if (journal.Registry is null || journal.Original.Length != areas.Length)
            throw new InvalidDataException("上次切换的恢复记录不完整。");

        await ensureClosed(false);
        var previousRegistry = ProtectedData.Unprotect(journal.Registry, null, DataProtectionScope.CurrentUser);
        var rollbackPath = journal.FullRegistry ? registryPath : $@"{registryPath}\{RegistryBranch.AuthKey}";
        RegistryBranch.Replace(rollbackPath, JsonSerializer.Deserialize<RegistryBranch>(previousRegistry));

        for (var i = 0; i < areas.Length; i++)
        {
            var area = areas[i];
            if (Disk.Exists(area.Backup))
            {
                Disk.RemoveDirectory(area.Live);
                Directory.Move(area.Backup, area.Live);
            }
            else if (!journal.Original[i] && !Disk.Exists(area.Stage))
            {
                Disk.RemoveDirectory(area.Live);
            }
            Disk.RemoveDirectory(area.Stage);
        }
        File.Delete(journalPath);
    }

    private void Complete(Journal journal, Catalog catalog)
    {
        Save(catalog with
        {
            LastRestored = journal.Restored,
            Accounts = catalog.Accounts.Select(item => item.Id == journal.Restored
                ? item with { LastUsed = journal.RestoredAt } : item).ToArray()
        });
        foreach (var area in WorkAreas(journal.Id))
        {
            Disk.RemoveDirectory(area.Backup);
            Disk.RemoveDirectory(area.Stage);
        }
        File.Delete(journalPath);
    }

    private WorkArea[] WorkAreas(Guid id) => roots.Select(root =>
        new WorkArea(root, root + $".switch-{id:N}.stage", root + $".switch-{id:N}.backup")).ToArray();

    private async Task<T> Exclusive<T>(Func<Task<T>> run)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await run().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private void Save(Catalog catalog)
    {
        Directory.CreateDirectory(dataFolder);
        Disk.Write(catalogPath, catalog);
    }

    private string SnapshotPath(Guid id) => Path.Combine(dataFolder, $"{id:N}.session");
    private string RequireLauncher() => FindLauncher() ?? throw new InvalidOperationException("找不到战网。请点「设置战网路径」选择启动程序。");
    private static Account Find(Catalog catalog, Guid id) =>
        catalog.Accounts.FirstOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("找不到所选账号。");

    private static string ValidateName(Catalog catalog, string name, Guid? id)
    {
        name = name.Trim();
        if (name.Length is 0 or > 36 || name.Any(char.IsControl))
            throw new InvalidOperationException("请输入 1–36 个字符的账号名称。");
        if (catalog.Accounts.Any(item => item.Id != id && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("已有同名账号，请换一个名称。");
        return name;
    }

    private sealed record WorkArea(string Live, string Stage, string Backup);
    private sealed record Journal(Guid Id, bool[]? Original, byte[]? Registry, Guid? Restored, DateTimeOffset? RestoredAt,
        bool FullRegistry = false)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Committed => RestoredAt is not null || Restored is not null;
    }
}

internal static class Client
{
    private static readonly string[] ProcessNames = ["Battle.net", "Battle.net Launcher"];
    private static readonly string[] Executables = ["Battle.net Launcher.exe", "Battle.net.exe"];

    public static bool IsLauncher(string? path) => !string.IsNullOrWhiteSpace(path) &&
        Executables.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) && File.Exists(path);

    public static string? FindLauncher(string? preferred)
    {
        if (preferred is not null)
            return IsLauncher(preferred) ? preferred : null;

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var entry in new[] { "", @"WOW6432Node\" })
        {
            var currentVersion = $@"SOFTWARE\{entry}Microsoft\Windows\CurrentVersion";
            foreach (var executable in Executables)
            {
                using var key = hive.OpenSubKey($@"{currentVersion}\App Paths\{executable}");
                var candidate = (key?.GetValue(null) as string)?.Trim('"');
                if (IsLauncher(candidate))
                    return candidate;
            }

            using var uninstall = hive.OpenSubKey($@"{currentVersion}\Uninstall\Battle.net");
            var folder = (uninstall?.GetValue("InstallLocation") as string)?.Trim('"');
            if (!string.IsNullOrWhiteSpace(folder))
            {
                var launcher = Executables.Select(exe => Path.Combine(folder, exe)).FirstOrDefault(File.Exists);
                if (launcher is not null)
                    return launcher;
            }
        }

        var running = ProcessPaths().FirstOrDefault();
        var folders = new[] { Path.GetDirectoryName(running),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Battle.net") };
        return folders.Where(folder => !string.IsNullOrEmpty(folder))
            .SelectMany(folder => Executables.Select(exe => Path.Combine(folder!, exe)))
            .FirstOrDefault(File.Exists) ?? running;
    }

    private static IEnumerable<string> ProcessPaths()
    {
        foreach (var name in ProcessNames)
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                string? path = null;
                try { path = process.MainModule?.FileName; }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
                if (IsLauncher(path))
                    yield return path!;
            }
        }
    }

    public static async Task EnsureClosed(bool requestClose)
    {
        var clock = Stopwatch.StartNew();
        var signaled = new HashSet<int>();
        do
        {
            var running = false;
            foreach (var name in ProcessNames)
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.HasExited)
                        continue;
                    running = true;
                    if (requestClose && signaled.Add(process.Id))
                    {
                        try { process.CloseMainWindow(); }
                        catch (InvalidOperationException) { }
                    }
                }
            }

            if (!running)
                return;
            if (!requestClose)
                break;
            await Task.Delay(250).ConfigureAwait(false);
        } while (clock.Elapsed < TimeSpan.FromSeconds(5));

        throw new InvalidOperationException("请从 Windows 托盘彻底退出战网，再重试。");
    }

    public static void Launch(string path, AccountRegion? region)
    {
        var start = new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = false };
        var code = region switch
        {
            AccountRegion.China => "CN",
            AccountRegion.Americas => "US",
            AccountRegion.Europe => "EU",
            AccountRegion.Asia => "KR",
            _ => null
        };
        if (code is not null)
            start.ArgumentList.Add($"--setregion={code}");
        using var process = Process.Start(start) ?? throw new IOException("未能启动战网客户端。");
    }
}
