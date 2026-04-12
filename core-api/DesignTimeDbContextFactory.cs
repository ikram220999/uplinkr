using core_api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace core_api;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var basePath = Directory.GetCurrentDirectory();
        var config = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = config.GetConnectionString("DefaultConnection")
            ?? "Server=127.0.0.1;Port=3306;Database=tunnel_core;User=root;Password=;";

        var versionString = config["MySql:ServerVersion"] ?? "8.0.36-mysql";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, ServerVersion.Parse(versionString))
            .Options;

        return new AppDbContext(options);
    }
}
