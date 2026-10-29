using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SaidNest.Capture;

sealed record KeyFile(int Version, string OwnerId, string ProtectedKey, string Salt, int Iterations, string RecoveryKey);

sealed class Vault : IDisposable
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    const int Iterations = 600_000;
    const int MaxObjectBytes = 8 * 1024 * 1024;
    const int MaxMetadataBytes = 4 * 1024 * 1024;
    readonly byte[] key;
    public string Root { get; }
    public string OwnerId { get; }

    Vault(string root, string ownerId, byte[] key) { Root = Path.GetFullPath(root); OwnerId = ownerId; this.key = key; }

    public static Vault Create(string root, string password)
    {
        if (password.Length < 12) throw new ArgumentException("Use a recovery passphrase of at least 12 characters.");
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Choose a new vault directory; existing paths are never initialized or overwritten.");
        Directory.CreateDirectory(root);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(acl);
        byte[] master = RandomNumberGenerator.GetBytes(32);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        string owner = Guid.NewGuid().ToString("N");
        byte[] wrapping = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            var info = new KeyFile(1, owner,
                Convert.ToBase64String(ProtectedData.Protect(master, Encoding.UTF8.GetBytes(owner), DataProtectionScope.CurrentUser)),
                Convert.ToBase64String(salt), Iterations,
                Convert.ToBase64String(Seal(master, wrapping, Encoding.UTF8.GetBytes("recovery:" + owner))));
            AtomicWrite(Path.Combine(root, "vault.json"), JsonSerializer.SerializeToUtf8Bytes(info, Json));
            Directory.CreateDirectory(Path.Combine(root, "sessions"));
            return new Vault(root, owner, master);
        }
        catch { CryptographicOperations.ZeroMemory(master); throw; }
        finally { CryptographicOperations.ZeroMemory(wrapping); }
    }

    static KeyFile ReadKeyFile(string root)
    {
        var path = Path.Combine(root, "vault.json");
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Oversized key file.");
        var info = JsonSerializer.Deserialize<KeyFile>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("Missing key metadata.");
        if (info.Version != 1 || !Guid.TryParseExact(info.OwnerId, "N", out _) || info.Iterations is < 600_000 or > 2_000_000)
            throw new InvalidDataException("Unsupported vault or recovery settings.");
        return info;
    }

    public static Vault Open(string root)
    {
        var info = ReadKeyFile(root);
        byte[] master = ProtectedData.Unprotect(Convert.FromBase64String(info.ProtectedKey), Encoding.UTF8.GetBytes(info.OwnerId), DataProtectionScope.CurrentUser);
        if (master.Length != 32) throw new CryptographicException("Invalid archive key.");
        return new Vault(root, info.OwnerId, master);
    }

    public static Vault OpenRecovery(string root, string password)
    {
        var info = ReadKeyFile(root);
        byte[] wrapping = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(info.Salt), info.Iterations, HashAlgorithmName.SHA256, 32);
        try { return new Vault(root, info.OwnerId, Unseal(Convert.FromBase64String(info.RecoveryKey), wrapping, Encoding.UTF8.GetBytes("recovery:" + info.OwnerId))); }
        finally { CryptographicOperations.ZeroMemory(wrapping); }
    }

    public void RestoreLocalUnlock()
    {
        var info = ReadKeyFile(Root);
        var updated = info with { ProtectedKey = Convert.ToBase64String(ProtectedData.Protect(key, Encoding.UTF8.GetBytes(OwnerId), DataProtectionScope.CurrentUser)) };
        AtomicWrite(Path.Combine(Root, "vault.json"), JsonSerializer.SerializeToUtf8Bytes(updated, Json), replace: true);
    }

    public string SessionPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid session ID.");
        return Path.Combine(Root, "sessions", id);
    }

    string Resolve(string relative)
    {
        string full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Object outside vault.");
        // Never follow a junction or symlink supplied as an archive object or parent directory.
        for (string? part = full; part != null && part.Length >= Root.Length; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Archive reparse points are unsupported.");
        return full;
    }

    public string Write(string relative, object metadata, ReadOnlySpan<byte> audio = default, bool replace = false)
    {
        byte[] header = JsonSerializer.SerializeToUtf8Bytes(metadata, Json);
        if (header.Length > MaxMetadataBytes || audio.Length > 2 * 1024 * 1024) throw new InvalidDataException("Archive object exceeds prototype limits.");
        byte[] payload = new byte[4 + header.Length + audio.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, header.Length);
        header.CopyTo(payload, 4);
        audio.CopyTo(payload.AsSpan(4 + header.Length));
        byte[] encrypted;
        try { encrypted = Seal(payload, key, Encoding.UTF8.GetBytes(OwnerId + ":" + relative.Replace('\\', '/'))); }
        finally { CryptographicOperations.ZeroMemory(payload); }
        AtomicWrite(Resolve(relative), encrypted, replace);
        return Convert.ToHexString(SHA256.HashData(encrypted)).ToLowerInvariant();
    }

    public void Replace(string relative, object metadata) => Write(relative, metadata, replace: true);

    public (JsonElement Metadata, byte[] Audio, string Hash) Read(string relative)
    {
        string path = Resolve(relative);
        if (new FileInfo(path).Length > MaxObjectBytes) throw new InvalidDataException("Oversized archive object.");
        byte[] encrypted;
        using (var input = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read | FileShare.Delete))
        {
            encrypted = new byte[checked((int)input.Length)];
            input.ReadExactly(encrypted);
        }
        byte[] plain = Unseal(encrypted, key, Encoding.UTF8.GetBytes(OwnerId + ":" + relative.Replace('\\', '/')));
        try
        {
            if (plain.Length < 4) throw new InvalidDataException("Truncated object.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(plain);
            if (length < 2 || length > MaxMetadataBytes || length > plain.Length - 4) throw new InvalidDataException("Invalid object header.");
            using var json = JsonDocument.Parse(plain.AsMemory(4, length));
            return (json.RootElement.Clone(), plain[(4 + length)..], Convert.ToHexString(SHA256.HashData(encrypted)).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    static byte[] Seal(ReadOnlySpan<byte> plain, byte[] secret, byte[] context)
    {
        byte[] result = new byte[32 + plain.Length];
        "SN01"u8.CopyTo(result);
        RandomNumberGenerator.Fill(result.AsSpan(4, 12));
        using var aes = new AesGcm(secret, 16);
        aes.Encrypt(result.AsSpan(4, 12), plain, result.AsSpan(32), result.AsSpan(16, 16), context);
        return result;
    }

    static byte[] Unseal(byte[] encrypted, byte[] secret, byte[] context)
    {
        if (encrypted.Length < 32 || !encrypted.AsSpan(0, 4).SequenceEqual("SN01"u8)) throw new InvalidDataException("Unknown encrypted object format.");
        byte[] plain = new byte[encrypted.Length - 32];
        using var aes = new AesGcm(secret, 16);
        aes.Decrypt(encrypted.AsSpan(4, 12), encrypted.AsSpan(32), encrypted.AsSpan(16, 16), plain, context);
        return plain;
    }

    internal static void AtomicWrite(string path, byte[] bytes, bool replace = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: replace);
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}
