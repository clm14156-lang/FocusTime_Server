using System.Security.Cryptography;
using FocusTime.Api.Modules.Membership;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
namespace FocusTime.Api.Tests;
public sealed class MembershipConfigurationTests
{
    private sealed class Host(string path, string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = path;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    [Theory, InlineData("Production"), InlineData("Staging")]
    public void NonDevelopmentRequiresConfiguredPersistentSigningKey(string environment)
    {
        var config = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => new EntitlementSigner(config, new Host("", environment)));
    }
    [Fact]
    public void DevelopmentKeyPersistsAcrossRestart_AndOnlyPublicMaterialIsExported()
    {
        var root = Path.Combine(Path.GetTempPath(), "membership-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var host = new Host(root, "Development"); var config = new ConfigurationBuilder().Build();
            string id;
            using (var first = new EntitlementSigner(config, host))
            {
                id = first.PublicKey.KeyId; using var publicKey = RSA.Create();
                publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(first.PublicKey.SubjectPublicKeyInfo), out _);
                Assert.Throws<CryptographicException>(() => publicKey.ExportParameters(true));
            }
            using var second = new EntitlementSigner(config, host); Assert.Equal(id, second.PublicKey.KeyId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory, InlineData(true), InlineData(false)]
    public void PublicOnlyOrUndersizedKeysCannotIssueLeases(bool publicOnly)
    {
        using var rsa = RSA.Create(publicOnly ? 2048 : 1024);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Membership:SigningKeyPem"] = publicOnly ? rsa.ExportSubjectPublicKeyInfoPem() : rsa.ExportPkcs8PrivateKeyPem() }).Build();
        Assert.ThrowsAny<Exception>(() => new EntitlementSigner(config, new Host("", "Production")));
    }
}
