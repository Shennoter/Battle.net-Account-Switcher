using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BattleNetSwitcher.Core;

internal static class SnapshotArchive
{
    public const int Version = 2;
    private const int Format = 0x32534E42;
    private const int LegacyFormat = 0x31534E42;
    public const string RefreshRequired = "旧版会话缺少完整的战网登录资料。请在战网重新登录后，使用「更新会话」。";
    private const int MacLength = 32;
    private const int IvLength = 16;
    private const int RegistryLimit = 16 * 1024 * 1024;
    private const byte FileEntry = 1;
    private const byte DirectoryEntry = 2;
    private const byte End = 0;

    public static void Write(string path, string[] roots, RegistryBranch registry)
    {
        var keys = RandomNumberGenerator.GetBytes(64);
        try
        {
            var wrapped = ProtectedData.Protect(keys, null, DataProtectionScope.CurrentUser);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var header = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            header.Write(Format);
            header.Write(wrapped.Length);
            header.Write(wrapped);
            var macPosition = stream.Position;
            header.Write(new byte[MacLength]);
            var payloadPosition = stream.Position;

            using var aes = MakeAes(keys, RandomNumberGenerator.GetBytes(IvLength));
            header.Write(aes.IV);
            using (var encryption = new CryptoStream(stream, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
            using (var gzip = new GZipStream(encryption, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(gzip))
            {
                var metadata = JsonSerializer.SerializeToUtf8Bytes(registry);
                if (metadata.Length > RegistryLimit)
                    throw new InvalidDataException("战网会话配置过大。");
                writer.Write(metadata.Length);
                writer.Write(metadata);

                for (var i = 0; i < roots.Length; i++)
                {
                    var present = Disk.Exists(roots[i]);
                    writer.Write(present);
                    if (present)
                        WriteDirectory(writer, roots[i], roots[i]);
                    writer.Write(End);
                }
            }

            stream.Position = payloadPosition;
            var mac = ComputeMac(keys, stream);
            stream.Position = macPosition;
            header.Write(mac);
            stream.Flush(true);
        }
        finally { CryptographicOperations.ZeroMemory(keys); }
    }

    public static RegistryBranch Extract(string path, string[] destinations)
    {
        using var stream = File.OpenRead(path);
        using var header = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var format = header.ReadInt32();
        if (format == LegacyFormat)
            throw new InvalidOperationException(RefreshRequired);
        if (format != Format)
            throw new InvalidDataException("无法识别此会话快照。");

        var wrapped = ReadExact(header, header.ReadInt32(), 4096);
        var mac = ReadExact(header, MacLength, MacLength);
        var payloadPosition = stream.Position;
        var keys = ProtectedData.Unprotect(wrapped, null, DataProtectionScope.CurrentUser);

        try
        {
            if (keys.Length != 64 || !CryptographicOperations.FixedTimeEquals(mac, ComputeMac(keys, stream)))
                throw new InvalidDataException("会话快照已损坏。");

            stream.Position = payloadPosition;
            using var aes = MakeAes(keys, ReadExact(header, IvLength, IvLength));
            using var encryption = new CryptoStream(stream, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var gzip = new GZipStream(encryption, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip);
            var registry = JsonSerializer.Deserialize<RegistryBranch>(
                ReadExact(reader, reader.ReadInt32(), RegistryLimit));
            if (registry?.HasSavedSession != true)
                throw new InvalidDataException("快照没有可恢复的战网登录记录。请重新登录后更新会话。");

            foreach (var destination in destinations)
            {
                var present = reader.ReadBoolean();
                if (present)
                    Directory.CreateDirectory(destination);

                for (var kind = reader.ReadByte(); kind != End; kind = reader.ReadByte())
                {
                    if (!present)
                        throw new InvalidDataException("会话快照的目录记录无效。");
                    var name = reader.ReadString();
                    var target = RestorePath(destination, name);
                    switch (kind)
                    {
                        case DirectoryEntry:
                            Directory.CreateDirectory(target);
                            break;
                        case FileEntry:
                            RestoreFile(reader, target);
                            break;
                        default:
                            throw new InvalidDataException("会话快照的文件记录无效。");
                    }
                }
            }

            if (gzip.ReadByte() != -1)
                throw new InvalidDataException("会话快照包含多余数据。");
            return registry;
        }
        finally { CryptographicOperations.ZeroMemory(keys); }
    }

    private static void WriteDirectory(BinaryWriter writer, string root, string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("战网数据目录包含链接，无法安全地保存快照。");

            var relative = Path.GetRelativePath(root, path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                writer.Write(DirectoryEntry);
                writer.Write(relative);
                WriteDirectory(writer, root, path);
            }
            else
            {
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                writer.Write(FileEntry);
                writer.Write(relative);
                writer.Write(source.Length);
                source.CopyTo(writer.BaseStream);
            }
        }
    }

    private static void RestoreFile(BinaryReader reader, string path)
    {
        var remaining = reader.ReadInt64();
        if (remaining < 0)
            throw new InvalidDataException("会话快照的文件长度无效。");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, buffer.Length);
                reader.BaseStream.ReadExactly(buffer.AsSpan(0, count));
                target.Write(buffer, 0, count);
                remaining -= count;
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static string RestorePath(string root, string name)
    {
        if (Path.IsPathRooted(name) || name.Contains(':'))
            throw new InvalidDataException("会话快照的文件路径无效。");

        var target = Path.GetFullPath(Path.Combine(root, name));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("会话快照的文件路径无效。");
        return target;
    }

    private static byte[] ReadExact(BinaryReader reader, int count, int limit)
    {
        if (count < 0 || count > limit)
            throw new InvalidDataException("会话快照的长度无效。");
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new EndOfStreamException("会话快照不完整。");
        return bytes;
    }

    private static Aes MakeAes(byte[] keys, byte[] iv)
    {
        var aes = Aes.Create();
        aes.Key = keys[..32];
        aes.IV = iv;
        return aes;
    }

    private static byte[] ComputeMac(byte[] keys, Stream stream)
    {
        using var hmac = new HMACSHA256(keys[32..]);
        return hmac.ComputeHash(stream);
    }
}
