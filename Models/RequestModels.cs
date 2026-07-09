namespace DeliAi.Backend.Models;

public record WrongInfoRequest(string? siteToken, string? response, string? name, bool fullyWrong, string? howToImprove);
public record ManageAuthRequest(string? siteToken, string? password);
public record WrongInfoReportDto(string? response, bool fullyWrong, string? howToImprove);
public record ResolveWrongInfoRequest(string? siteToken, string? password, WrongInfoReportDto? report, string? correctedInfo);
public record WrongInfoDismiss(string? siteToken, string? password, string? report);
