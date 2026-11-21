using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Exceptions;
using Security_Camera_Backend.Filters;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services;
using Security_Camera_Backend.Services.Auth;
using Security_Camera_Backend.Services.Cv;
using Security_Camera_Backend.Services.ExternalAuth;
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
    builder.Services.AddHostedService<AdminUserSeeder>();

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer();

    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<IOptions<JwtOptions>>((options, jwtOptionsAccessor) =>
        {
            var jwt = jwtOptionsAccessor.Value;

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
    builder.Services.AddSingleton<INotificationDispatcher, NoOpNotificationDispatcher>();

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
// Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseHttpsRedirection();

    // Serve saved JPEGs at /captures so the frontend can link directly to them.
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(capturesDirectory),
        RequestPath = storageOptions.GetPublicRequestPath()
    });

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Exposed so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program { }
