using DeliAi.Backend.Auth;
using DeliAi.Backend.Config;
using DeliAi.Backend.Models;
using DeliAi.Backend.Services;

namespace DeliAi.Backend.Endpoints;

public static class WrongInfoEndpoints
{
  public static void MapWrongInfoEndpoints(this WebApplication app)
  {
    app.MapPost("/wrong-info", async (HttpRequest request, DeliBot bot, TokenService tokenService) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<WrongInfoRequest>(body);

      if (!tokenService.ValidateSiteToken(data?.siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      if (string.IsNullOrWhiteSpace(data?.response) || string.IsNullOrWhiteSpace(data?.howToImprove))
        return Results.BadRequest(new { error = "Missing required fields." });

      string name = string.IsNullOrWhiteSpace(data.name) ? "Anonymous" : data.name;
      bool success = await bot.LogWrongInfoReport(data.response, name, data.fullyWrong, data.howToImprove);

      return Results.Json(new { success });
    });

    app.MapPost("/wrong-info/list", async (HttpRequest request, DeliBot bot, TokenService tokenService) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<WrongInfoRequest>(body);

      if (!tokenService.ValidateSiteToken(data?.siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      List<object> result = await bot.GetWrongInfoReports();
      return Results.Json(result);
    });

    app.MapPost("/wrong-info/resolve", async (HttpRequest request, DeliBot bot, TokenService tokenService, EnvironmentConfig config) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<ResolveWrongInfoRequest>(body);

      if (!tokenService.ValidateSiteToken(data?.siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      if (data?.password != config.ManagePassword)
        return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

      if (string.IsNullOrWhiteSpace(data?.report?.response) || string.IsNullOrWhiteSpace(data?.correctedInfo) || data == null)
      {
        return Results.Json(new { error = "Invalid information" }, statusCode: 401);
      }

      bool status = await bot.ResolveWrongInfo(data.report.response, data.correctedInfo);

      return Results.Json(new { success = status });
    });

    app.MapPost("/wrong-info/dismiss", async (HttpRequest request, DeliBot bot, TokenService tokenService, EnvironmentConfig config) =>
    {
      using var reader = new StreamReader(request.Body);
      var body = await reader.ReadToEndAsync();
      var data = System.Text.Json.JsonSerializer.Deserialize<ResolveWrongInfoRequest>(body);

      if (!tokenService.ValidateSiteToken(data?.siteToken))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

      if (data?.password != config.ManagePassword)
        return Results.Json(new { error = "Invalid manage password" }, statusCode: 401);

      if (data?.report == null || string.IsNullOrWhiteSpace(data.report.response))
        return Results.Json(new { error = "Invalid information" }, statusCode: 400);

      bool success = await bot.DismissWrongInfoReport(data.report.response);

      return Results.Json(new { success });
    });
  }
}
