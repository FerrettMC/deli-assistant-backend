using DeliAi.Backend.Auth;
using DeliAi.Backend.Config;

namespace DeliAi.Backend.Endpoints;

public static class SiteEndpoints
{
  public static void MapSiteEndpoints(this WebApplication app)
  {
    // 👇 Site-wide login — required before using the chat at all
    app.MapPost("/site/login", async (HttpRequest request, TokenService tokenService, EnvironmentConfig config) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
      var password = data?.GetValueOrDefault("password");

      if (password != config.SitePassword)
        return Results.Json(new { success = false });

      string token = tokenService.GenerateSiteToken();
      return Results.Json(new { success = true, token });
    });
  }
}
