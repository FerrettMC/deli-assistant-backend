using DeliAi.Backend.Auth;
using DeliAi.Backend.Services;

namespace DeliAi.Backend.Endpoints;

public static class ChatEndpoints
{
  public static void MapChatEndpoints(this WebApplication app)
  {
    // 👇 Chat endpoint requires a valid site token
    app.MapPost("/getAI", async (HttpRequest request, DeliBot bot, TokenService tokenService) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
      var siteToken = data?.GetValueOrDefault("siteToken");
      var message = data?.GetValueOrDefault("message");

      if (!tokenService.ValidateSiteToken(siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      if (string.IsNullOrWhiteSpace(message))
        return Results.BadRequest(new { error = "No message provided." });

      var answer = await bot.Ask(message);
      return Results.Json(new { response = answer });
    });
  }
}
