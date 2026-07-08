using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace DeliAi.Backend;

// This is ONLY used by the `dotnet ef` CLI tool when generating/applying migrations
// from your machine. Your actual running app still uses Program.cs's DI setup.
public class DeliDbContextFactory : IDesignTimeDbContextFactory<DeliDbContext>
{
  public DeliDbContext CreateDbContext(string[] args)
  {
    string connectionString = BuildConnectionString();

    var optionsBuilder = new DbContextOptionsBuilder<DeliDbContext>();
    optionsBuilder.UseNpgsql(connectionString);

    return new DeliDbContext(optionsBuilder.Options);
  }

  private static string BuildConnectionString()
  {
    string? databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

    if (!string.IsNullOrWhiteSpace(databaseUrl))
    {
      var uri = new Uri(databaseUrl);
      var userInfo = uri.UserInfo.Split(':', 2);

      var csBuilder = new NpgsqlConnectionStringBuilder
      {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Username = userInfo[0],
        Password = userInfo.Length > 1 ? userInfo[1] : "",
        Database = uri.AbsolutePath.TrimStart('/'),
        SslMode = SslMode.Require
      };

      return csBuilder.ConnectionString;
    }

    return Environment.GetEnvironmentVariable("LOCAL_DB_CONNECTION")
        ?? "Host=localhost;Port=5432;Database=deliai;Username=postgres;Password=postgres";
  }
}