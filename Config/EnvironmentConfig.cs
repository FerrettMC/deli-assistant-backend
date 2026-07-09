using Npgsql;

namespace DeliAi.Backend.Config;

public class EnvironmentConfig
{
  public string FrontendUrl { get; }
  public string SitePassword { get; }
  public string ManagePassword { get; }
  public string JwtSecret { get; }

  public EnvironmentConfig()
  {
    FrontendUrl = Environment.GetEnvironmentVariable("FRONTEND_URL") ?? "http://localhost:5173";
    SitePassword = Environment.GetEnvironmentVariable("SITE_PASSWORD")
        ?? throw new Exception("SITE_PASSWORD environment variable not set.");
    ManagePassword = Environment.GetEnvironmentVariable("MANAGE_PASSWORD")
        ?? throw new Exception("MANAGE_PASSWORD environment variable not set.");
    JwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
        ?? throw new Exception("JWT_SECRET environment variable not set.");
  }

  // Railway (and most hosts) inject a DATABASE_URL like:
  //   postgres://user:password@host:port/dbname
  // Npgsql wants a different key=value format, so convert it.
  public string BuildConnectionString()
  {
    string? databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

    if (string.IsNullOrWhiteSpace(databaseUrl))
    {
      // Local dev fallback - put a normal Npgsql connection string in your .env, e.g.:
      // LOCAL_DB_CONNECTION=Host=localhost;Port=5432;Database=deliai;Username=postgres;Password=postgres
      return Environment.GetEnvironmentVariable("LOCAL_DB_CONNECTION")
          ?? throw new Exception("Neither DATABASE_URL nor LOCAL_DB_CONNECTION is set.");
    }

    var uri = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(':', 2);

    var csBuilder = new NpgsqlConnectionStringBuilder
    {
      Host = uri.Host,
      Port = uri.Port > 0 ? uri.Port : 5432,
      Username = userInfo[0],
      Password = userInfo.Length > 1 ? userInfo[1] : "",
      Database = uri.AbsolutePath.TrimStart('/'),
      SslMode = SslMode.Prefer
    };

    return csBuilder.ConnectionString;
  }
}
