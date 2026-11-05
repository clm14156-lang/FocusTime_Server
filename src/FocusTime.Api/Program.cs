using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FocusTime.Api.Common;
using FocusTime.Api.Modules.Admin;
using Microsoft.AspNetCore.Authorization;
using FocusTime.Api.Infrastructure.Health;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Sessions;
using FocusTime.Api.Modules.FocusSync;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication;
using FocusTime.Api.Modules.Membership;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<MembershipService>();
builder.Services.AddScoped<MembershipOrderService>();
builder.Services.AddSingleton<EntitlementSigner>();

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "O";
    options.UseUtcTimestamp = true;
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = ApiLimits.MaximumRequestBytes);

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    ApiProblems.Enrich(context.HttpContext, context.ProblemDetails));
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase, allowIntegerValues: false)))
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Request validation failed."
        };
        ApiProblems.Enrich(context.HttpContext, problem);
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    });
builder.Services.AddOpenApi();
const string WebsiteAuthCors = "WebsiteAuth";
var websiteOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? (builder.Environment.IsDevelopment()
        ? ["http://127.0.0.1:5173", "http://localhost:5173", "http://127.0.0.1:5175", "http://localhost:5175"]
        : []);
foreach (var origin in websiteOrigins)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttps && !(builder.Environment.IsDevelopment() &&
            uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
        uri.UserInfo.Length != 0 || uri.GetLeftPart(UriPartial.Authority) != origin)
        throw new InvalidOperationException("Cors:AllowedOrigins must contain exact HTTPS origins or Development HTTP loopback origins.");
}
builder.Services.AddCors(options => options.AddPolicy(WebsiteAuthCors, policy => policy
    .WithOrigins(websiteOrigins)
    .WithMethods("POST", "GET")
    .WithHeaders("Content-Type")
    .WithExposedHeaders("Retry-After")));
builder.Services.AddOptions<RequestRateLimitOptions>()
    .BindConfiguration("RateLimiting")
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var limits = context.RequestServices.GetRequiredService<IOptions<RequestRateLimitOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.PermitLimit,
                Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Ceiling(retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ApiProblems.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests,
            "Too many requests.", cancellationToken: cancellationToken);
    };
});

var connectionString = builder.Configuration.GetConnectionString("Database");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Configure ConnectionStrings:Database before starting the application.");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString,
    postgres => postgres.MigrationsHistoryTable("__ef_migrations_history", "public")));
builder.Services.AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddPasswordValidator<LetterPasswordValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AuthSecurity>();
builder.Services.AddScoped<IEmailSender, EmailSender>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<DeviceSessionService>();
builder.Services.AddScoped<FocusSyncService>();
builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddSingleton<AdminAccess>();
builder.Services.AddSingleton<AdminMonitor>();
builder.Services.AddHostedService<AdminMonitorPoller>();
builder.Services.AddAuthorization();
builder.Services.AddOptions<AuthorizationOptions>().Configure<AdminAccess>((options, admins) =>
    options.AddPolicy(AdminAccess.Policy, policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(context => admins.Allows(context.User))));
builder.Services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"]);

var app = builder.Build();

if (args.Contains("--membership-admin", StringComparer.Ordinal))
{
    await MembershipAdmin.RunAsync(args, app.Services);
    return;
}

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    app.Logger.LogInformation("Database migrations completed.");
    return;
}

_ = app.Services.GetRequiredService<EntitlementSigner>();
app.UseMiddleware<RequestTraceMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages(context => ApiProblems.WriteAsync(context.HttpContext,
    context.HttpContext.Response.StatusCode));
app.UseRouting();
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/v1/auth") || context.Request.Path == "/api/v1/membership-plans",
    branch => branch.UseCors(WebsiteAuthCors));
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.ContentLength > ApiLimits.MaximumRequestBytes)
    {
        await ApiProblems.WriteAsync(context, StatusCodes.Status413PayloadTooLarge);
        return;
    }
    await next(context);
});
app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthResponseWriter.WriteAsync
}).DisableRateLimiting().AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteAsync
}).DisableRateLimiting().AllowAnonymous();
if (app.Environment.IsDevelopment())
    app.MapOpenApi().DisableRateLimiting();

await app.RunAsync();

public partial class Program { }
