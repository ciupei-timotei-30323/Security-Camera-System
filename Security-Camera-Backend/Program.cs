using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Models;


DotNetEnv.Env.TraversePath().Load();
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
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



builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();