using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Controllers;
using Security_Camera_Backend.Exceptions;
using Security_Camera_Backend.Filters;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services;
using Security_Camera_Backend.Services.Auth;
using Security_Camera_Backend.Services.Camera;
using Security_Camera_Backend.Services.Cv;
using Security_Camera_Backend.Services.ExternalAuth;
using Security_Camera_Backend.Services.Notifications;
using Security_Camera_Backend.Services.Security;
using Security_Camera_Backend.Services.Settings;
using Security_Camera_Backend.Services.Systems;
using Security_Camera_Backend.Services.Users;
using Serilog.Configuration;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();
try
{
    
    // Real environment variables (Docker/systemd) win over a local .env file.
    DotNetEnv.Env.NoClobber().TraversePath().Load();
    var builder = WebApplication.CreateBuilder(args);

    // Fail fast: a non-Development host must never boot with the repository's sample secrets
    // (dev API keys, factory-pass accounts, a short/known JWT key, the external-auth stub).
    // "Testing" is exempt because integration tests deliberately run the stub and short keys.
    var isDevelopmentLike = builder.Environment.IsDevelopment()
                            || builder.Environment.IsEnvironment("Testing");
    ProductionSecretValidator.Validate(builder.Configuration, isDevelopmentLike);

    // Upload limits. Kestrel enforces the ceiling before the request is read, so an oversized
    // body is rejected without buffering it. Per-endpoint limits are applied with
    // [RequestSizeLimit] on the ingestion actions.
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = RequestLimits.MaxBodyBytes;
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
        options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    });

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services),true); 

    
    Log.Information("Connecting to the DB...");
// Add services to the container.
// Adding the Database connection
    var connString = builder.Configuration["DB_URL"];

    builder.Services.AddDbContext<SecurityCameraBackendContext>(options => options.UseNpgsql(connString,

            // This enables retry on dead connections
            npgsqlOptionsAction:
            sqlOptions =>
            {
                sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(2),
                    errorCodesToAdd: null);

                sqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);


            }
        )
    );

    Log.Information("DB connection established");
    
    // Adding ProblemDetails and GlobalExceptionHandler
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    
    // Strongly-typed configuration
    builder.Services.Configure<PiAuthOptions>(builder.Configuration.GetSection(PiAuthOptions.SectionName));
    builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
    builder.Services.Configure<CvServiceOptions>(builder.Configuration.GetSection(CvServiceOptions.SectionName));
    builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.SectionName));
    builder.Services.Configure<ExternalAuthOptions>(builder.Configuration.GetSection(ExternalAuthOptions.SectionName));
    builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection(NotificationOptions.SectionName));
    builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
    builder.Services.Configure<AlarmOptions>(builder.Configuration.GetSection(AlarmOptions.SectionName));
    builder.Services.Configure<LoginRateLimitOptions>(builder.Configuration.GetSection(LoginRateLimitOptions.SectionName));
    builder.Services.Configure<LoginLockoutOptions>(builder.Configuration.GetSection(LoginLockoutOptions.SectionName));
    builder.Services.Configure<CorsOptions>(builder.Configuration.GetSection(CorsOptions.SectionName));

    // Development defaults to the configurable stub; production defaults to the real factory IAM.
    var useStubExternalAuth = builder.Configuration.GetValue<bool?>("ExternalAuth:UseStub")
                              ?? builder.Environment.IsDevelopment();
    var externalAuthBaseUrl = builder.Configuration["ExternalAuth:BaseUrl"];

    if (!useStubExternalAuth && string.IsNullOrWhiteSpace(externalAuthBaseUrl))
    {
        throw new InvalidOperationException(
            "ExternalAuth:BaseUrl is required when ExternalAuth:UseStub is false.");
    }

    builder.Services.AddHttpClient(HttpExternalAdminAuthClient.HttpClientName, (serviceProvider, client) =>
    {
        var externalAuth = serviceProvider.GetRequiredService<IOptions<ExternalAuthOptions>>().Value;

        client.BaseAddress = new Uri(externalAuth.BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, externalAuth.TimeoutSeconds));
    });

    if (useStubExternalAuth)
    {
        builder.Services.AddScoped<IExternalAdminAuthClient, StubExternalAdminAuthClient>();
    }
    else
    {
        builder.Services.AddScoped<IExternalAdminAuthClient, HttpExternalAdminAuthClient>();
    }

    builder.Services.AddOptions<JwtOptions>()
        .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
        .Validate(o => !string.IsNullOrWhiteSpace(o.Secret), "Jwt:Secret is required.")
        .Validate(o => !string.IsNullOrWhiteSpace(o.Secret) &&
                       Encoding.UTF8.GetByteCount(o.Secret) >= JwtOptions.MinimumSecretBytes,
            $"Jwt:Secret must be at least {JwtOptions.MinimumSecretBytes} bytes long.")
        .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer), "Jwt:Issuer is required.")
        .Validate(o => !string.IsNullOrWhiteSpace(o.Audience), "Jwt:Audience is required.")
        .Validate(o => o.ExpirationMinutes > 0, "Jwt:ExpirationMinutes must be greater than zero.")
        .ValidateOnStart();

    // Dashboard user authentication (JWT Bearer). The Pi API key stays a per-endpoint
    // action filter, so /api/events is unaffected by this default scheme.
    builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
    builder.Services.AddScoped<IAuthService, AuthService>();
    builder.Services.AddScoped<ITokenRevocationService, TokenRevocationService>();
    builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
    builder.Services.AddScoped<ISystemRegistry, SystemRegistry>();
    builder.Services.AddScoped<IUserManagementService, UserManagementService>();
    builder.Services.AddSingleton<LoginLockoutTracker>();
    builder.Services.AddHostedService<AdminUserSeeder>();

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer();

    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<IOptions<JwtOptions>, IOptions<PiAuthOptions>>((options, jwtOptionsAccessor, piAuthAccessor) =>
        {
            var jwt = jwtOptionsAccessor.Value;
            var nodePiAuth = piAuthAccessor.Value;

            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = AuthClaimTypes.Name,
                RoleClaimType = AuthClaimTypes.Role
            };

            options.Events = new JwtBearerEvents
            {
                // A JWT is only as good as its jti: reject tokens that were explicitly logged out.
                OnTokenValidated = async context =>
                {
                    // Reject tokens minted for a different Pi when this node is pinned.
                    if (!NodeSystemBinding.IsTokenBoundToNode(
                            context.Principal?.FindFirstValue(AuthClaimTypes.SystemId),
                            nodePiAuth))
                    {
                        context.Fail("Token belongs to a different camera system.");
                        return;
                    }

                    var jti = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);

                    // Tokens issued by this API always carry a jti. If one is missing there is nothing to
                    // look up; individual endpoints (logout) reject it with a 400.
                    if (string.IsNullOrWhiteSpace(jti))
                    {
                        return;
                    }

                    var tokenRevocationService = context.HttpContext.RequestServices
                        .GetRequiredService<ITokenRevocationService>();

                    if (await tokenRevocationService.IsRevokedAsync(jti, context.HttpContext.RequestAborted))
                    {
                        context.Fail("Token has been revoked.");
                    }
                },
                // Keep challenge responses as ProblemDetails, like the rest of the API.
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                    await context.Response.WriteAsJsonAsync(
                        new ProblemDetails
                        {
                            Status = StatusCodes.Status401Unauthorized,
                            Title = "Unauthorized",
                            Detail = "The access token is missing, invalid, expired, or revoked.",
                            Instance = context.Request.Path
                        },
                        context.HttpContext.RequestAborted);
                }
            };
        });

    // Motion ingestion pipeline
    builder.Services.AddScoped<PiApiKeyAuthFilter>();
    builder.Services.AddSingleton<ICaptureStorage, CaptureStorage>();
    builder.Services.AddScoped<IDetectionProcessingService, DetectionProcessingService>();
    builder.Services.AddScoped<IEventIngestionService, EventIngestionService>();
    builder.Services.AddScoped<IAwayModeSettingsService, AwayModeSettingsService>();
    builder.Services.AddScoped<ISystemStateService, SystemStateService>();
    builder.Services.AddScoped<IEventQueryService, EventQueryService>();

    // Outbound alerts use a bounded channel so POST /api/events never waits for SMTP.
    builder.Services.AddSingleton<SuspectAlertQueue>();
    builder.Services.AddSingleton<EmailCooldownTracker>();
    builder.Services.AddSingleton<INotificationDispatcher, QueuedNotificationDispatcher>();
    builder.Services.AddSingleton<ISmtpSender, SmtpSender>();
    builder.Services.AddScoped<INotifier, EmailNotifier>();
    builder.Services.AddHostedService<SuspectAlertDispatcher>();
    builder.Services.AddSingleton<IAlarmStateStore, AlarmStateStore>();

    // Live preview is process-local: only one JPEG per system is retained.
    builder.Services.AddSingleton<ILatestFrameStore, LatestFrameStore>();
    builder.Services.AddSingleton<IFrameStreamLimiter, FrameStreamLimiter>();
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<ICvServiceHealthService, CvServiceHealthService>();
    builder.Services.AddScoped<ISystemStatusService, SystemStatusService>();
    builder.Services.AddSingleton(TimeProvider.System);

    var loginRateLimit = builder.Configuration
        .GetSection(LoginRateLimitOptions.SectionName)
        .Get<LoginRateLimitOptions>() ?? new LoginRateLimitOptions();
    var trustForwardedHeaders = builder.Configuration.GetValue<bool>("ReverseProxy:TrustForwardedHeaders");

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(CameraController.FrameRateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Request.Headers[PiApiKeyAuthFilter.HeaderName].ToString(),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));

        // Strict per-client-IP limit on credential submission: password guessing is the most
        // attractive endpoint on the whole API, and no legitimate browser needs more than a few
        // attempts per minute.
        options.AddPolicy(AuthController.LoginRateLimitPolicy, httpContext =>
            loginRateLimit.Enabled
                ? RateLimitPartition.GetFixedWindowLimiter(
                    GetLoginRateLimitPartitionKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, loginRateLimit.PermitLimit),
                        Window = TimeSpan.FromSeconds(Math.Max(1, loginRateLimit.WindowSeconds)),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    })
                : RateLimitPartition.GetNoLimiter("login-rate-limit-disabled"));

        // Keep the 429 shape consistent with the rest of the API (ProblemDetails, not an empty body).
        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.HttpContext.Response.Headers.RetryAfter = "60";

            await context.HttpContext.Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests",
                    Detail = "Too many login attempts. Try again later.",
                    Instance = context.HttpContext.Request.Path
                },
                ct);
        };
    });

    // The backend is not reachable by browsers directly: the dashboard BFF proxies every call
    // same-origin. CORS is therefore off unless an explicit allow-list is configured (never a
    // wildcard, which would let any origin drive the API with a stolen token).
    var allowedCorsOrigins = builder.Configuration
        .GetSection(CorsOptions.SectionName)
        .Get<CorsOptions>()?.AllowedOrigins ?? [];

    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    {
        if (allowedCorsOrigins.Length == 0)
        {
            // No origins: the policy stays restrictive so the CORS middleware never emits a
            // wildcard header. Requests without an allowed origin are simply not CORS-enabled.
            policy.SetIsOriginAllowed(_ => false);
            return;
        }

        policy.WithOrigins(allowedCorsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    }));

    builder.Services.AddHttpClient<ICvServiceClient, CvServiceClient>((serviceProvider, client) =>
    {
        var cvOptions = serviceProvider.GetRequiredService<IOptions<CvServiceOptions>>().Value;

        client.BaseAddress = new Uri(cvOptions.BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, cvOptions.TimeoutSeconds));
    });

    builder.Services.AddControllers();
    
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOpenApi();

    // Captures live on a shared Docker volume, never in PostgreSQL.
    var storageOptions = builder.Configuration
        .GetSection(StorageOptions.SectionName)
        .Get<StorageOptions>() ?? new StorageOptions();
    var capturesDirectory = storageOptions.GetCapturesDirectoryFullPath();
    Directory.CreateDirectory(capturesDirectory);
    Log.Information("Captures directory ready at {CapturesDirectory}", capturesDirectory);

    var app = builder.Build();
    app.UseSerilogRequestLogging();

    app.UseExceptionHandler();

    // Swagger/OpenAPI describes every endpoint and schema to an anonymous reader; in production
    // that is attack-surface documentation for free. Development only.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseHttpsRedirection();
    app.UseCors();

    // Captures are intentionally not served as static files: images are only reachable through
    // the tenant-scoped /api/events/{id}/face and /annotated endpoints.
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    app.MapControllers();

    app.Run();

    // SECURITY: the partition key is the TCP peer address. X-Forwarded-For is client-controlled
    // unless a trusted proxy rewrites it, so honouring it by default would let an attacker bypass
    // the limit by rotating a header. Set ReverseProxy:TrustForwardedHeaders=true only when the
    // backend sits behind a proxy that overwrites the header (see appsettings comments).
    string GetLoginRateLimitPartitionKey(HttpContext httpContext)
    {
        if (trustForwardedHeaders)
        {
            var forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].ToString();

            if (!string.IsNullOrWhiteSpace(forwardedFor))
            {
                var firstHop = forwardedFor.Split(',', 2)[0].Trim();

                if (firstHop.Length > 0)
                {
                    return firstHop;
                }
            }
        }

        return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-client";
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");

    // A swallowed exception would exit with status 0, which tells Docker/systemd the process
    // finished cleanly — exactly the opposite of what a fail-fast startup check means.
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Exposed so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program { }
