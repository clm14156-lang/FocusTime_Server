using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.Membership;
using FocusTime.Api.Modules.Membership.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FocusTime.Api.Tests;

public sealed class DevelopmentMembershipTests
{
    [Theory]
    [InlineData("Development", true, true, MembershipKind.Lifetime)]
    [InlineData("Development", true, false, MembershipKind.Normal)]
    [InlineData("Development", false, true, MembershipKind.Normal)]
    [InlineData("Production", true, true, MembershipKind.Normal)]
    [InlineData("Staging", true, true, MembershipKind.Normal)]
    public void OnlyConfirmedConfiguredUserReceivesDevelopmentMembership(string environment,
        bool confirmed, bool matchingUser, MembershipKind expected)
    {
        var configuredUser = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"DevelopmentMemberships:{configuredUser:D}"] = "lifetime"
        }).Build();
        var service = new DevelopmentMemberships(new TestEnvironment { EnvironmentName = environment }, configuration);
        var user = new UserProfile(matchingUser ? configuredUser : Guid.NewGuid(), "vip-test@example.test", confirmed);
        Assert.Equal(expected.ToString().ToLowerInvariant(), service.Apply(user).Membership);
    }

    [Fact]
    public void ReloadingConfigurationRevokesGrantAndIgnoresInputMembership()
    {
        var id = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var service = new DevelopmentMemberships(new TestEnvironment(), configuration);
        var user = new UserProfile(id, "vip-test@example.test", true) { Membership = "lifetime" };
        Assert.Equal("normal", service.Apply(user).Membership);
        configuration[$"DevelopmentMemberships:{id:D}"] = "lifetime";
        Assert.Equal("lifetime", service.Apply(user).Membership);
        configuration[$"DevelopmentMemberships:{id:D}"] = null;
        Assert.Equal("normal", service.Apply(user).Membership);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "FocusTime.Api.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
