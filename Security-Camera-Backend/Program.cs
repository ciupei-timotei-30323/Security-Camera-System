using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Exceptions;
using Security_Camera_Backend.Filters;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services;
using Security_Camera_Backend.Services.Cv;
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

    // Motion ingestion pipeline
    builder.Services.AddScoped<PiApiKeyAuthFilter>();
    builder.Services.AddScoped<IEventIngestionService, EventIngestionService>();
    builder.Services.AddSingleton<ISystemStateService, SystemStateService>();
    builder.Services.AddSingleton<INotificationDispatcher, NoOpNotificationDispatcher>();

    builder.Services.AddHttpClient<ICvServiceClient, CvServiceClient>((serviceProvider, client) =>
    {
        var cvOptions = serviceProvider.GetRequiredService<IOptions<CvServiceOptions>>().Value;

        client.BaseAddress = new Uri(cvOptions.BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(cvOptions.TimeoutSeconds);
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
