using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace BattleNetSwitcher.Core;

public enum AccountRegion { China, Americas, Europe, Asia }

public sealed record Account(Guid Id, string Name, AccountRegion Region, Guid Snapshot,
    DateTimeOffset SavedAt, DateTimeOffset? LastUsed = null, int SnapshotVersion = 1)
{
    [JsonIgnore]
    public bool NeedsRefresh => SnapshotVersion < SnapshotArchive.Version;

    [JsonIgnore]
    public bool IsCurrent { get; init; }

    [JsonIgnore]
    public string RegionName => Region switch
    {
        AccountRegion.China => "国服",
        AccountRegion.Americas => "美洲",
        AccountRegion.Europe => "欧洲",
        _ => "亚洲"
    };

    [JsonIgnore]
    public string Monogram => Region switch
    {
        AccountRegion.China => "CN",
        AccountRegion.Americas => "US",
        AccountRegion.Europe => "EU",
        _ => "KR"
    };

    [JsonIgnore]
    public string Initial => System.Globalization.StringInfo.GetNextTextElement(Name);
}

public sealed record Catalog(Account[] Accounts, string? Launcher = null, Guid? LastRestored = null);

internal static class Disk
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllBytes(path))
        ?? throw new InvalidDataException("数据文件为空。");

    public static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryClean(() => File.Delete(temp));
        }
    }

    public static bool Exists(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("战网数据目录包含链接，无法安全地保存或替换。");
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException("战网数据目录已被同名文件占用。");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static void RemoveDirectory(string path)
    {
        if (Exists(path))
            Directory.Delete(path, recursive: true);
    }

    public static void TryClean(Action action)
    {
        try { action(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed record RegistryBranch(RegistryEntry[] Values, Dictionary<string, RegistryBranch> Children)
{
    public const string AuthKey = "UnifiedAuth";

    [JsonIgnore]
    public bool HasSavedSession => Children.Any(child =>
        string.Equals(child.Key, AuthKey, StringComparison.OrdinalIgnoreCase) && child.Value.ContainsValues());

    private bool ContainsValues() => Values.Length > 0 || Children.Values.Any(child => child.ContainsValues());

    public static RegistryBranch? Read(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key is null ? null : Read(key);
    }

    private static RegistryBranch Read(RegistryKey key)
    {
        var values = key.GetValueNames().Select(name =>
        {
            var kind = key.GetValueKind(name);
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null)
                throw new IOException("战网会话状态在保存期间发生了变化。");

            return new RegistryEntry(name, kind, JsonSerializer.SerializeToElement(value));
        }).ToArray();

        var children = new Dictionary<string, RegistryBranch>();
        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name) ?? throw new IOException("战网会话状态在保存期间发生了变化。");
            children.Add(name, Read(child));
        }
        return new RegistryBranch(values, children);
    }

    public static void Replace(string path, RegistryBranch? branch)
    {
        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        if (branch is null)
            return;

        using var key = Registry.CurrentUser.CreateSubKey(path);
        branch.Write(key);
    }

    public void RestoreSession(string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        // Keep launcher settings outside the snapshot, but never mix two sets of auth entries.
        key.DeleteSubKeyTree(AuthKey, throwOnMissingSubKey: false);
        Write(key);
    }

    private void Write(RegistryKey key)
    {
        foreach (var entry in Values)
        {
            object value = entry.Kind switch
            {
                RegistryValueKind.DWord => entry.Value.GetInt32(),
                RegistryValueKind.QWord => entry.Value.GetInt64(),
                RegistryValueKind.Binary or RegistryValueKind.None => entry.Value.GetBytesFromBase64(),
                RegistryValueKind.MultiString => entry.Value.Deserialize<string[]>()!,
                RegistryValueKind.String or RegistryValueKind.ExpandString => entry.Value.GetString()!,
                _ => throw new InvalidDataException("战网会话包含不支持的注册表值类型。")
            };
            key.SetValue(entry.Name, value, entry.Kind);
        }

        foreach (var (name, branch) in Children)
        {
            using var child = key.CreateSubKey(name);
            branch.Write(child);
        }
    }
}

internal sealed record RegistryEntry(string Name, RegistryValueKind Kind, JsonElement Value);
