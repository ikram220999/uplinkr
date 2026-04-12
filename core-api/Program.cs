using System.Runtime.InteropServices;
using core_api;
using core_api.Data;
using core_api.Services;
using Docker.DotNet;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "core-api", Version = "v1" });
});

var dockerEndpoint = builder.Configuration["Docker:Endpoint"];
if (string.IsNullOrWhiteSpace(dockerEndpoint))
{
    dockerEndpoint = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "npipe://./pipe/docker_engine"
        : "unix:///var/run/docker.sock";
}

builder.Services.AddSingleton<IDockerClient>(_ =>
    new DockerClientConfiguration(new Uri(dockerEndpoint)).CreateClient());
builder.Services.Configure<TunnelingOptions>(builder.Configuration.GetSection(TunnelingOptions.SectionName));
builder.Services.AddSingleton<INginxTunnelConfigWriter, NginxTunnelConfigWriter>();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

var serverVersion = ServerVersion.Parse(
    builder.Configuration["MySql:ServerVersion"] ?? "8.0.36-mysql");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, serverVersion));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "core-api v1");
    });
}

app.MapControllers();
app.MapGet("/", () => Results.Ok(new { service = "core-api", swagger = "/swagger", openapi = "/swagger/v1/swagger.json" }));

app.Run();
