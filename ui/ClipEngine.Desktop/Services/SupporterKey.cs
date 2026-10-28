using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ClipEngine.Desktop.Services;

// Supporter keys thank people who back Clip Engine on Patreon. They are made with tools/SupporterKeys, which signs
// the patron's name with a private key that only the developer has. The app checks the signature with the public key
// below, offline, so a key works forever with no account and no server.
internal static class SupporterConfig
{
    // Printed by `SupporterKeys init`. While it's empty the app doesn't offer a key box.
    public const string PublicKey = "";

    public const string PatreonUrl = "";

    public static bool KeysEnabled => PublicKey.Length > 0;
}

internal sealed record Supporter(string Name, DateOnly Since);

internal static class SupporterKey
{
    public const string Prefix = "CE1-";
    private const byte Version = 1;
    private const int SignatureBytes = 64;
    private const int MaxNameBytes = 64;
    private static readonly DateOnly Epoch = new(2026, 1, 1);

    // Layout before base64url: version (1) | days since 2026-01-01 (2) | name length (1) | name (UTF-8) | signature (64).
    public static string Create(ECDsa privateKey, string name, DateOnly issued)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name.Trim());
        if (nameBytes.Length is 0 or > MaxNameBytes) throw new ArgumentException($"The name must be 1 to {MaxNameBytes} bytes.", nameof(name));

        var payload = new byte[4 + nameBytes.Length];
        payload[0] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), (ushort)(issued.DayNumber - Epoch.DayNumber));
        payload[3] = (byte)nameBytes.Length;
        nameBytes.CopyTo(payload, 4);

        var signature = privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Prefix + Base64Url([.. payload, .. signature]);
    }

    // Returns who the key belongs to, or null when it isn't a genuine key.
    public static Supporter? Verify(string key, string publicKey)
    {
        key = new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (publicKey.Length == 0 || !key.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        byte[] bytes;
        byte[] spki;
        try
        {
            bytes = FromBase64Url(key[Prefix.Length..]);
            spki = Convert.FromBase64String(publicKey);
        }
        catch (FormatException)
        {
            return null;
        }
        if (bytes.Length < 4 + 1 + SignatureBytes || bytes[0] != Version) return null;
        var nameLength = bytes[3];
        if (bytes.Length != 4 + nameLength + SignatureBytes) return null;

        var payload = bytes.AsSpan(0, 4 + nameLength);
        var signature = bytes.AsSpan(4 + nameLength);
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
        }
        catch (CryptographicException)
        {
            return null;
        }
        if (!ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            return null;
        }

        string name;
        try
        {
            name = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(payload[4..]);
        }
        catch (ArgumentException)
        {
            return null;
        }
        var since = DateOnly.FromDayNumber(Epoch.DayNumber + BinaryPrimitives.ReadUInt16BigEndian(payload[1..]));
        return new Supporter(name, since);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        text = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '='));
    }
}

// The key saved on this PC, in %LOCALAPPDATA%\Clip_Engine\supporter.key.
internal static class SupporterKeyFile
{
    public static string? Load(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string file, string? key)
    {
        if (key is null)
        {
            File.Delete(file);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, key);
    }
}
