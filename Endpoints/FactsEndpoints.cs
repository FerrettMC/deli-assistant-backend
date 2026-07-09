using DeliAi.Backend.Auth;
using DeliAi.Backend.Config;
using DeliAi.Backend.Services;

namespace DeliAi.Backend.Endpoints;

public static class FactsEndpoints
{
  // 👇 All facts endpoints require BOTH a valid site token AND the manage password on every call
  public static void MapFactsEndpoints(this WebApplication app)
  {
    app.MapPost("/facts/list", async (HttpRequest request, DeliBot bot, TokenService tokenService, EnvironmentConfig config) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
      var siteToken = data?.GetValueOrDefault("siteToken");
      var password = data?.GetValueOrDefault("password");

      if (!tokenService.ValidateSiteToken(siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      if (password != config.ManagePassword)
        return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

      return Results.Json(await bot.GetAllFactsAsync());
    });

    app.MapPost("/facts/add", async (HttpRequest request, DeliBot bot, TokenService tokenService, EnvironmentConfig config) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
      var siteToken = data?.GetValueOrDefault("siteToken");
      var password = data?.GetValueOrDefault("password");
      var info = data?.GetValueOrDefault("info");

      if (!tokenService.ValidateSiteToken(siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      if (password != config.ManagePassword)
        return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

      if (string.IsNullOrWhiteSpace(info))
        return Results.BadRequest(new { error = "No info provided." });

      var result = await bot.AddFactAsync(info);
      return Results.Json(new { response = result });
    });

    app.MapPost("/facts/edit/{index}", async (int index, HttpRequest request, DeliBot bot, TokenService tokenService, EnvironmentConfig config) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
      var siteToken = data?.GetValueOrDefault("siteToken");
      var password = data?.GetValueOrDefault("password");
      var info = data?.GetValueOrDefault("info");

      if (!tokenService.ValidateSiteToken(siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      if (password != config.ManagePassword)
        return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

      if (string.IsNullOrWhiteSpace(info))
        return Results.BadRequest(new { error = "No info provided." });

      var result = await bot.EditFactByIndexAsync(index, info);
      return Results.Json(new { response = result });
    });

    app.MapPost("/facts/delete/{index}", async (int index, HttpRequest request, DeliBot bot, TokenService tokenService, EnvironmentConfig config) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
      var siteToken = data?.GetValueOrDefault("siteToken");
      var password = data?.GetValueOrDefault("password");

      if (!tokenService.ValidateSiteToken(siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      if (password != config.ManagePassword)
        return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

      var result = await bot.DeleteFactByIndexAsync(index);
      return Results.Json(new { response = result });
    });

    // returns array of at max 3 items ? !
    app.MapPost("/facts/suggestions", async (HttpRequest request, DeliBot bot, TokenService tokenService, EnvironmentConfig config) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
      var siteToken = data?.GetValueOrDefault("siteToken");
      var password = data?.GetValueOrDefault("password");

      if (!tokenService.ValidateSiteToken(siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      if (password != config.ManagePassword)
        return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

      List<string> result = await bot.getSuggestions();
      return Results.Json(result);
    });

    app.MapPost("/facts/suggestions/remove", async (HttpRequest request, DeliBot bot, TokenService tokenService, EnvironmentConfig config) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);

      var siteToken = data?.GetValueOrDefault("siteToken");
      var password = data?.GetValueOrDefault("password");
      var suggestion = data?.GetValueOrDefault("suggestion");

      if (!tokenService.ValidateSiteToken(siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      if (password != config.ManagePassword)
        return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

      if (string.IsNullOrWhiteSpace(suggestion))
        return Results.Json(new { error = "Missing suggestion" }, statusCode: 400);

      await bot.removeSuggestion(suggestion);

      List<string> result = await bot.getSuggestions();
      return Results.Json(result);
    });

    app.MapPost("/facts/bulk-add", async (HttpRequest request, DeliBot bot, TokenService tokenService, EnvironmentConfig config) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
      var siteToken = data?.GetValueOrDefault("siteToken");
      var password = data?.GetValueOrDefault("password");
      var infoBlock = data?.GetValueOrDefault("infoBlock");

      if (!tokenService.ValidateSiteToken(siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      if (password != config.ManagePassword)
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

    app.MapPost("/facts/add-answer", async (HttpRequest request, DeliBot bot, TokenService tokenService, EnvironmentConfig config) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
      var siteToken = data?.GetValueOrDefault("siteToken");
      var password = data?.GetValueOrDefault("password");
      var suggestion = data?.GetValueOrDefault("suggestion");
      var answer = data?.GetValueOrDefault("answer");

      if (!tokenService.ValidateSiteToken(siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      if (password != config.ManagePassword)
        return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

      if (string.IsNullOrWhiteSpace(suggestion) || string.IsNullOrWhiteSpace(answer))
        return Results.BadRequest(new { error = "Missing suggestion or answer." });

      var result = await bot.AddAnsweredSuggestionAsync(suggestion, answer);
      return Results.Json(new { response = result });
    });
  }
}
