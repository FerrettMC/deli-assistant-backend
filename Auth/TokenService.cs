using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DeliAi.Backend.Config;
using Microsoft.IdentityModel.Tokens;

namespace DeliAi.Backend.Auth;

public class TokenService
{
  private readonly string _jwtSecret;

  public TokenService(EnvironmentConfig config)
  {
    _jwtSecret = config.JwtSecret;
  }

  public string GenerateSiteToken()
  {
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        claims: new[] { new Claim("role", "site-user") },
        expires: DateTime.UtcNow.AddDays(90), // ~3 months
        signingCredentials: creds
    );

    return new JwtSecurityTokenHandler().WriteToken(token);
  }

  public bool ValidateSiteToken(string? token)
  {
    if (string.IsNullOrWhiteSpace(token))
    {
      Console.WriteLine("[Token check] Token was null or empty.");
      return false;
    }

    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
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
}
