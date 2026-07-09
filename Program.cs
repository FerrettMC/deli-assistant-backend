using DeliAi.Backend;
using DeliAi.Backend.Auth;
using DeliAi.Backend.Config;
using DeliAi.Backend.Endpoints;
using DeliAi.Backend.Services;
using DotNetEnv;
using Microsoft.EntityFrameworkCore;

Env.Load();

var builder = WebApplication.CreateBuilder(args);

var config = new EnvironmentConfig();
builder.Services.AddSingleton(config);

builder.Services.AddDbContextFactory<DeliDbContext>(options =>
    options.UseNpgsql(config.BuildConnectionString()));

builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<EmailAlertService>();
builder.Services.AddSingleton<DeliBot>();

builder.Services.AddCors(options =>
{
  options.AddPolicy("AllowFrontend", policy =>
  {
    policy.WithOrigins(config.FrontendUrl)
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

app.MapGet("/", () => "Hello World!");

app.MapSiteEndpoints();
app.MapChatEndpoints();
app.MapManageEndpoints();
app.MapFactsEndpoints();
app.MapWrongInfoEndpoints();

app.Run();
