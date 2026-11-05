using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FocusTime.Api.Modules.Membership;

public sealed record EntitlementPublicKey(string KeyId, string Algorithm, string SubjectPublicKeyInfo);

public sealed class EntitlementSigner : IDisposable
{
    private readonly RSA rsa = RSA.Create();
    private readonly object gate = new();
    public EntitlementPublicKey PublicKey { get; }
    public EntitlementSigner(IConfiguration configuration, IHostEnvironment environment)
    {
        var pem = configuration["Membership:SigningKeyPem"];
        var path = configuration["Membership:SigningKeyPath"];
        if (string.IsNullOrWhiteSpace(pem))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (!environment.IsDevelopment()) throw new InvalidOperationException("Configure a persistent Membership:SigningKeyPath in production.");
                path = Path.Combine(environment.ContentRootPath, ".artifacts", "membership-signing-key.pem");
            }
            if (!File.Exists(path) && environment.IsDevelopment())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using var generated = RSA.Create(3072);
                try
                {
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var bytes = Encoding.UTF8.GetBytes(generated.ExportPkcs8PrivateKeyPem()); file.Write(bytes);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (IOException) when (File.Exists(path)) { }
            }
            pem = File.ReadAllText(path);
        }
        rsa.ImportFromPem(pem);
        // Reject public-only material and undersized keys before issuing any proof.
        _ = rsa.ExportParameters(true);
        if (rsa.KeySize < 2048) throw new InvalidOperationException("Membership signing key must be RSA >= 2048 bits.");
        var spki = rsa.ExportSubjectPublicKeyInfo();
        PublicKey = new(Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(), "RS256", Convert.ToBase64String(spki));
    }
    public string Sign(object claims)
    {
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid = PublicKey.KeyId }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var input = header + "." + payload;
        lock (gate) return input + "." + Encode(rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public void Dispose() => rsa.Dispose();
}
