using DeliAi.Backend;
using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

Env.Load();

var builder = WebApplication.CreateBuilder(args);

string frontendUrl = Environment.GetEnvironmentVariable("FRONTEND_URL") ?? "http://localhost:5173";
string sitePassword = Environment.GetEnvironmentVariable("SITE_PASSWORD")
    ?? throw new Exception("SITE_PASSWORD environment variable not set.");
string managePassword = Environment.GetEnvironmentVariable("MANAGE_PASSWORD")
    ?? throw new Exception("MANAGE_PASSWORD environment variable not set.");
string jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? throw new Exception("JWT_SECRET environment variable not set.");

// ---- Database wiring ----
// Railway (and most hosts) inject a DATABASE_URL like:
//   postgres://user:password@host:port/dbname
// Npgsql wants a different key=value format, so convert it.
string BuildConnectionString()
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

builder.Services.AddDbContextFactory<DeliDbContext>(options =>
    options.UseNpgsql(BuildConnectionString()));

builder.Services.AddSingleton<DeliBot>();

builder.Services.AddCors(options =>
{
  options.AddPolicy("AllowFrontend", policy =>
  {
    policy.WithOrigins(frontendUrl)
            .AllowAnyHeader()
            .AllowAnyMethod();
  });
});

var app = builder.Build();
app.UseCors("AllowFrontend");

// Apply any pending EF Core migrations automatically on startup.
using (var scope = app.Services.CreateScope())
{
  var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DeliDbContext>>();
  await using var db = await factory.CreateDbContextAsync();
  await db.Database.MigrateAsync();
}

var bot = app.Services.GetRequiredService<DeliBot>();
await bot.EnsureDefaultRuleAsync();

string GenerateSiteToken()
{
  var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
  var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

  var token = new JwtSecurityToken(
      claims: new[] { new Claim("role", "site-user") },
      expires: DateTime.UtcNow.AddDays(90), // ~3 months
      signingCredentials: creds
  );

  return new JwtSecurityTokenHandler().WriteToken(token);
}

bool ValidateSiteToken(string? token)
{
  if (string.IsNullOrWhiteSpace(token))
  {
    Console.WriteLine("[Token check] Token was null or empty.");
    return false;
  }

  var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
  var handler = new JwtSecurityTokenHandler();

  try
  {
    var principal = handler.ValidateToken(token, new TokenValidationParameters
    {
      ValidateIssuer = false,
      ValidateAudience = false,
      ValidateLifetime = true,
      IssuerSigningKey = key
    }, out _);

    bool hasRole = principal.Claims.Any(c =>
        (c.Type == ClaimTypes.Role || c.Type == "role") && c.Value == "site-user");

    Console.WriteLine($"[Token check] Valid signature. Has correct role claim: {hasRole}");
    return hasRole;
  }
  catch (Exception ex)
  {
    Console.WriteLine($"[Token check] Validation threw: {ex.GetType().Name} — {ex.Message}");
    return false;
  }
}

app.MapGet("/", () => "Hello World!");

// 👇 Site-wide login — required before using the chat at all
app.MapPost("/site/login", async (HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
  var password = data?.GetValueOrDefault("password");

  if (password != sitePassword)
    return Results.Json(new { success = false });

  string token = GenerateSiteToken();
  return Results.Json(new { success = true, token });
});

// 👇 Chat endpoint now requires a valid site token
app.MapPost("/getAI", async (HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
  var siteToken = data?.GetValueOrDefault("siteToken");
  var message = data?.GetValueOrDefault("message");

  if (!ValidateSiteToken(siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  if (string.IsNullOrWhiteSpace(message))
    return Results.BadRequest(new { error = "No message provided." });

  var answer = await bot.Ask(message);
  return Results.Json(new { response = answer });
});

// 👇 Manage password check — NOT stored as a JWT, checked fresh every time
app.MapPost("/manage/verify", async (HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
  var siteToken = data?.GetValueOrDefault("siteToken");
  var password = data?.GetValueOrDefault("password");

  if (!ValidateSiteToken(siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  bool valid = password == managePassword;
  return Results.Json(new { success = valid });
});

// 👇 All facts endpoints require BOTH a valid site token AND the manage password on every call
app.MapPost("/facts/list", async (HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
  var siteToken = data?.GetValueOrDefault("siteToken");
  var password = data?.GetValueOrDefault("password");

  if (!ValidateSiteToken(siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  if (password != managePassword)
    return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

  return Results.Json(await bot.GetAllFactsAsync());
});

app.MapPost("/facts/add", async (HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
  var siteToken = data?.GetValueOrDefault("siteToken");
  var password = data?.GetValueOrDefault("password");
  var info = data?.GetValueOrDefault("info");

  if (!ValidateSiteToken(siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  if (password != managePassword)
    return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

  if (string.IsNullOrWhiteSpace(info))
    return Results.BadRequest(new { error = "No info provided." });

  var result = await bot.AddFactAsync(info);
  return Results.Json(new { response = result });
});

app.MapPost("/facts/edit/{index}", async (int index, HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
  var siteToken = data?.GetValueOrDefault("siteToken");
  var password = data?.GetValueOrDefault("password");
  var info = data?.GetValueOrDefault("info");

  if (!ValidateSiteToken(siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  if (password != managePassword)
    return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

  if (string.IsNullOrWhiteSpace(info))
    return Results.BadRequest(new { error = "No info provided." });

  var result = await bot.EditFactByIndexAsync(index, info);
  return Results.Json(new { response = result });
});

app.MapPost("/facts/delete/{index}", async (int index, HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
  var siteToken = data?.GetValueOrDefault("siteToken");
  var password = data?.GetValueOrDefault("password");

  if (!ValidateSiteToken(siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  if (password != managePassword)
    return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

  var result = await bot.DeleteFactByIndexAsync(index);
  return Results.Json(new { response = result });
});

// returns array of at max 3 items ? !
app.MapPost("/facts/suggestions", async (HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
  var siteToken = data?.GetValueOrDefault("siteToken");
  var password = data?.GetValueOrDefault("password");

  if (!ValidateSiteToken(siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  if (password != managePassword)
    return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

  List<string> result = await bot.getSuggestions();
  return Results.Json(result);
});

app.MapPost("/facts/suggestions/remove", async (HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);

  var siteToken = data?.GetValueOrDefault("siteToken");
  var password = data?.GetValueOrDefault("password");
  var suggestion = data?.GetValueOrDefault("suggestion");

  if (!ValidateSiteToken(siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  if (password != managePassword)
    return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

  if (string.IsNullOrWhiteSpace(suggestion))
    return Results.Json(new { error = "Missing suggestion" }, statusCode: 400);

  await bot.removeSuggestion(suggestion);

  List<string> result = await bot.getSuggestions();
  return Results.Json(result);
});

app.MapPost("/facts/bulk-add", async (HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
  var siteToken = data?.GetValueOrDefault("siteToken");
  var password = data?.GetValueOrDefault("password");
  var infoBlock = data?.GetValueOrDefault("infoBlock");

  if (!ValidateSiteToken(siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  if (password != managePassword)
    return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

  if (string.IsNullOrWhiteSpace(infoBlock))
    return Results.BadRequest(new { error = "No info provided." });

  var items = infoBlock
      .Split('\n')
      .Select(s => s.Trim())
      .Where(s => !string.IsNullOrWhiteSpace(s))
      .ToList();

  var result = await bot.AddFactsBulkAsync(items);
  return Results.Json(new { response = result });
});

app.MapPost("/facts/add-answer", async (HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
  var siteToken = data?.GetValueOrDefault("siteToken");
  var password = data?.GetValueOrDefault("password");
  var suggestion = data?.GetValueOrDefault("suggestion");
  var answer = data?.GetValueOrDefault("answer");

  if (!ValidateSiteToken(siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  if (password != managePassword)
    return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

  if (string.IsNullOrWhiteSpace(suggestion) || string.IsNullOrWhiteSpace(answer))
    return Results.BadRequest(new { error = "Missing suggestion or answer." });

  var result = await bot.AddAnsweredSuggestionAsync(suggestion, answer);
  return Results.Json(new { response = result });
});

app.MapPost("/wrong-info", async (HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<WrongInfoRequest>(body);

  if (!ValidateSiteToken(data?.siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  if (string.IsNullOrWhiteSpace(data?.response) || string.IsNullOrWhiteSpace(data?.howToImprove))
    return Results.BadRequest(new { error = "Missing required fields." });

  string name = string.IsNullOrWhiteSpace(data.name) ? "Anonymous" : data.name;
  bool success = await bot.LogWrongInfoReport(data.response, name, data.fullyWrong, data.howToImprove);

  return Results.Json(new { success });
});

app.MapPost("/wrong-info/list", async (HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<WrongInfoRequest>(body);

  if (!ValidateSiteToken(data?.siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  List<object> result = await bot.GetWrongInfoReports();
  return Results.Json(result);
});

app.MapPost("/wrong-info/resolve", async (HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<ResolveWrongInfoRequest>(body);

  if (!ValidateSiteToken(data?.siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  if (data?.password != managePassword)
    return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

  if (string.IsNullOrWhiteSpace(data?.report?.response) || string.IsNullOrWhiteSpace(data?.correctedInfo) || data == null)
  {
    return Results.Json(new { error = "Invalid information" }, statusCode: 401);
  }

  bool status = await bot.ResolveWrongInfo(data.report.response, data.correctedInfo);

  return Results.Json(new { success = status });
});

app.MapPost("/wrong-info/dismiss", async (HttpRequest request) =>
{
  using var reader = new StreamReader(request.Body);
  var body = await reader.ReadToEndAsync();
  var data = System.Text.Json.JsonSerializer.Deserialize<ResolveWrongInfoRequest>(body);

  if (!ValidateSiteToken(data?.siteToken))
    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

  if (data?.password != managePassword)
    return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

  if (data?.report == null || string.IsNullOrWhiteSpace(data.report.response))
    return Results.Json(new { error = "Invalid information" }, statusCode: 400);

  bool success = await bot.DismissWrongInfoReport(data.report.response);

  return Results.Json(new { success });
});

app.Run();

public record WrongInfoRequest(string? siteToken, string? response, string? name, bool fullyWrong, string? howToImprove);
public record ManageAuthRequest(string? siteToken, string? password);
public record WrongInfoReportDto(string? response, bool fullyWrong, string? howToImprove);
public record ResolveWrongInfoRequest(string? siteToken, string? password, WrongInfoReportDto? report, string? correctedInfo);
public record WrongInfoDismiss(string? siteToken, string? password, string? report);