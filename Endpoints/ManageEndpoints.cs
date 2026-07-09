using DeliAi.Backend.Auth;
using DeliAi.Backend.Config;

namespace DeliAi.Backend.Endpoints;

public static class ManageEndpoints
{
  public static void MapManageEndpoints(this WebApplication app)
  {
    // 👇 Manage password check — NOT stored as a JWT, checked fresh every time
    app.MapPost("/manage/verify", async (HttpRequest request, TokenService tokenService, EnvironmentConfig config) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
      var siteToken = data?.GetValueOrDefault("siteToken");
      var password = data?.GetValueOrDefault("password");

      if (!tokenService.ValidateSiteToken(siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      bool valid = password == config.ManagePassword;
      return Results.Json(new { success = valid });
    });
  }
}
