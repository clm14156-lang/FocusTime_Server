using System.Security.Cryptography;
using System.Text;

namespace FocusTime.Api.Modules.Identity;

public sealed class AuthFault(int status, string code, string message, int? retryAfterSeconds = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed class AuthSecurity(IConfiguration configuration, IHostEnvironment environment)
{
    private readonly Lazy<byte[]> key = new(() => LoadKey(configuration, environment));
    public void EnsureConfigured() => _ = key.Value;
    public static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public string Derive(string value) => Convert.ToHexString(HMACSHA256.HashData(key.Value, Encoding.UTF8.GetBytes(value)));
    public string CodeHash(EmailChallenge challenge, string code) => Derive(
        $"email-code:{challenge.Id:N}:{challenge.Purpose}:{challenge.Email}:{code}");
    public static bool Equal(string first, string second) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));

    private static byte[] LoadKey(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Authentication:Secret"];
        if (string.IsNullOrWhiteSpace(configured) && environment.IsDevelopment())
        {
            var path = Path.Combine(environment.ContentRootPath, ".artifacts", "auth-secret.key");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // CreateNew prevents concurrent processes from overwriting an existing development key.
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(RandomNumberGenerator.GetBytes(32));
            }
            catch (IOException) when (File.Exists(path)) { }
            var local = File.ReadAllBytes(path);
            if (local.Length >= 32) return local;
            throw new AuthFault(503, "authentication_unavailable", "The development authentication key is invalid.");
        }
        try
        {
            var bytes = Convert.FromBase64String(configured ?? "");
            if (bytes.Length >= 32) return bytes;
        }
        catch (FormatException) { }
        throw new AuthFault(503, "authentication_unavailable", "Authentication secret is not configured.");
    }
}
