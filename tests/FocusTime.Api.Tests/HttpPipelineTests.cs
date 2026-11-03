using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FocusTime.Api.Common;

namespace FocusTime.Api.Tests;

public sealed class HttpPipelineTests
{
    [Fact]
    public async Task ServiceInfo_AndLiveness_WorkWithoutDatabase()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var info = await client.GetFromJsonAsync<ServiceInfo>("/api/v1/system/info");
        Assert.Equal("v1", info?.ApiVersion);
        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("healthy", (await Json(response)).GetProperty("status").GetString());
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", new string('A', 64));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/system/info")).StatusCode);
    }

    [Fact]
    public async Task Readiness_UnavailableDatabase_Returns503WithoutDetails()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unhealthy", body);
        Assert.DoesNotContain("Username", body);
        Assert.DoesNotContain("Exception", body);
    }

    [Theory]
    [InlineData("/does-not-exist", "GET", 404, "not_found")]
    [InlineData("/api/v1/system/info", "POST", 405, "method_not_allowed")]
    public async Task RoutingErrors_UseProblemDetails(string path, string method, int status, string code)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        await AssertProblem(response, status, code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"focus\",\"minutes\":0}")]
    [InlineData("{broken")]
    public async Task InvalidBody_UsesValidationProblem(string body)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsync("/api/v1/test-probe/validation",
            new StringContent(body, Encoding.UTF8, "application/json"));
        await AssertProblem(response, 400, "validation_failed");
        Assert.True((await Json(response)).TryGetProperty("errors", out _));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/xml")]
    public async Task UnhandledException_IsSanitized_EvenWithUnsupportedAccept(string accept)
    {
        await using var factory = new ApiFactory(environment: "Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        var response = await client.GetAsync("/api/v1/test-probe/throw");
        await AssertProblem(response, 500, "internal_error");
        Assert.DoesNotContain("do-not-expose", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RateLimit_Returns429_AndDoesNotLimitHealthChecks()
    {
        await using var factory = new ApiFactory(permitLimit: 2);
        using var client = factory.CreateClient();
        (await client.GetAsync("/api/v1/system/info")).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/v1/system/info")).EnsureSuccessStatusCode();
        var response = await client.GetAsync("/api/v1/system/info");
        await AssertProblem(response, 429, "rate_limit_exceeded");
        Assert.True(response.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task LargeBody_Returns413()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var content = new ByteArrayContent(new byte[ApiLimits.MaximumRequestBytes + 1]);
        var response = await client.PostAsync("/api/v1/test-probe/validation", content);
        await AssertProblem(response, 413, "payload_too_large");
    }

    [Fact]
    public async Task UnsupportedContentType_UsesProblemDetails()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsync("/api/v1/test-probe/validation",
            new StringContent("name=focus&minutes=25", Encoding.UTF8, "text/plain"));
        await AssertProblem(response, 415, "unsupported_media_type");
    }

    [Theory]
    [InlineData("Development", 200)]
    [InlineData("Production", 404)]
    public async Task OpenApi_IsExposedOnlyInDevelopment(string environment, int status)
    {
        await using var factory = new ApiFactory(environment: environment);
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(status, (int)response.StatusCode);
        if (status == 200)
            Assert.True((await Json(response)).GetProperty("paths").TryGetProperty("/api/v1/system/info", out _));
    }

    [Fact]
    public async Task TraceId_IsServerGenerated_AndMatchesErrorBody()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Trace-Id", "untrusted-client-value");
        client.DefaultRequestHeaders.Add("traceparent", "00-11111111111111111111111111111111-2222222222222222-01");
        var first = await client.GetAsync("/missing");
        var second = await client.GetAsync("/missing");
        var trace = first.Headers.GetValues("X-Trace-Id").Single();
        Assert.Equal(trace, (await Json(first)).GetProperty("traceId").GetString());
        Assert.NotEqual("untrusted-client-value", trace);
        Assert.NotEqual(trace, second.Headers.GetValues("X-Trace-Id").Single());
    }

    private static async Task AssertProblem(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await Json(response);
        Assert.Equal(status, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
