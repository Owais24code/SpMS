using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Host.Identity;

public sealed record GuestSessionOptions(byte[] SigningKey, TimeSpan Lifetime);

/// <summary>HMAC-SHA256 JWTs, issuer spms-guest. Validated by the SpmsGuest scheme.</summary>
public sealed class GuestSessionIssuer(GuestSessionOptions options, IClock clock) : IGuestSessionIssuer
{
    public const string Issuer = "spms-guest";
    public const string Audience = "spms";

    public GuestSession Issue(GuestSessionClaims c)
    {
        var now = clock.UtcNow;
        var expires = now.Add(options.Lifetime);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, c.PrincipalId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Uuid7.New().ToString()),
            new(SpmsClaims.TenantId, c.TenantId.ToString()),
            new(SpmsClaims.PrincipalId, c.PrincipalId.ToString()),
            new(SpmsClaims.GuestId, c.GuestId.ToString()),
            new(SpmsClaims.PropertyId, c.PropertyId.ToString()),
            new(SpmsClaims.Properties, string.Join(' ', c.PropertyIds)),
            new(SpmsClaims.ActorType, nameof(ActorType.Guest)),
            new(SpmsClaims.EffectiveScope, SpaScopes.GuestSelf),
            new(SpmsClaims.SessionPurpose, c.Purpose),
            new("preferred_username", c.DisplayName),
        };
        if (c.ScopeEntityType is not null && c.ScopeEntityId is { } e)
            claims.Add(new Claim(SpmsClaims.SessionEntity, $"{c.ScopeEntityType}:{e}"));

        var token = new JwtSecurityToken(
            issuer: Issuer, audience: Audience, claims: claims,
            notBefore: now.UtcDateTime, expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(options.SigningKey), SecurityAlgorithms.HmacSha256));
        return new GuestSession(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public static TokenValidationParameters Validation(byte[] key) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "preferred_username",
    };

    /// <summary>
    /// The signing key. Required outside Development; in Development a fixed,
    /// published, worthless key keeps sessions valid across restarts.
    /// </summary>
    public static byte[] KeyFrom(string? base64, IHostEnvironment env)
    {
        if (!string.IsNullOrWhiteSpace(base64))
        {
            var k = Convert.FromBase64String(base64);
            if (k.Length < 32) throw new InvalidOperationException("Auth:GuestSigningKey must be at least 32 bytes.");
            return k;
        }
        if (!env.IsDevelopment())
            throw new InvalidOperationException("Auth:GuestSigningKey is not configured.");
        return SHA256.HashData("spms-development-guest-session-key"u8);
    }
}
