using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Models;
using Serilog.Configuration;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();
try
{
    
    DotNetEnv.Env.TraversePath().Load();
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services),true); // True otherwise Logger freezes in Testing (idk why)

    
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
    
    builder.Services.AddControllers();
    
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOpenApi();

    var app = builder.Build();
    app.UseSerilogRequestLogging();

    
// Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseHttpsRedirection();

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