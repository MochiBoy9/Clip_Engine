using System.Security.Cryptography;
using ClipEngine.Desktop.Services;

// Makes supporter keys for Patreon backers.
//
//   dotnet run --project ui/ClipEngine.SupporterKeys -- init            once: creates the signing key, prints the public key
//   dotnet run --project ui/ClipEngine.SupporterKeys -- make "Name"     prints a key for a patron
//
// The private key is kept outside the repository. Never commit or share it: anyone who has it can make keys.
var keyFile = Environment.GetEnvironmentVariable("CLIPENGINE_SIGNING_KEY") ??
              Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipEngine-SupporterKeys", "private.pem");

switch (args.FirstOrDefault())
{
    case "init":
        if (File.Exists(keyFile))
        {
            Console.Error.WriteLine($"A signing key already exists at {keyFile}. Making a new one would stop every key you've sent from working.");
            return 1;
        }
        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
            File.WriteAllText(keyFile, ecdsa.ExportPkcs8PrivateKeyPem());
            Console.WriteLine($"Signing key saved to {keyFile}. Back it up somewhere private.");
            Console.WriteLine();
            Console.WriteLine("Put this public key in SupporterConfig.PublicKey (ui/ClipEngine.Desktop/Services/SupporterKey.cs):");
            Console.WriteLine(Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
        }
        return 0;

    case "make" when args.Length >= 2:
        if (!File.Exists(keyFile))
        {
            Console.Error.WriteLine($"No signing key at {keyFile}. Run `init` first.");
            return 1;
        }
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportFromPem(File.ReadAllText(keyFile));
            var key = SupporterKey.Create(ecdsa, string.Join(' ', args[1..]), DateOnly.FromDateTime(DateTime.Today));
            Console.WriteLine(key);
        }
        return 0;

    case "public":
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportFromPem(File.ReadAllText(keyFile));
            Console.WriteLine(Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
        }
        return 0;

    default:
        Console.WriteLine("""
            Usage:
              SupporterKeys init           create the signing key (once) and print the public key
              SupporterKeys make "Name"    print a supporter key for a patron
              SupporterKeys public         print the public key again
            """);
        return 1;
}
