using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Spms.SharedKernel;

namespace Spms.Host.Identity;

/// <summary>
/// Auth:Local — SpMS's own email + password accounts, for deployments without
/// Entra ID. Can run alongside Entra or on its own.
/// </summary>
public sealed class LocalAuthOptions
{
    public bool Enabled { get; set; }
    /// <summary>Base64 HMAC key, 32+ bytes (Key Vault / app setting). Development falls back to a fixed, public key.</summary>
    public string? SigningKey { get; set; }
    public int SessionHours { get; set; } = 12;
    /// <summary>A Register page: sign-ups wait, with no roles, until an administrator approves them.</summary>
    public bool AllowRegistration { get; set; } = true;
    /// <summary>Tenant and property codes a sign-up joins (default: the Provision section's first tenant and property).</summary>
    public string? RegistrationTenant { get; set; }
    public string? RegistrationProperty { get; set; }
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}

public sealed record LocalSession(string Token, DateTimeOffset ExpiresAt, bool MustChangePassword);

/// <summary>HMAC-SHA256 JWTs, issuer spms-local; validated by the SpmsLocal scheme and then resolved like an Entra token.</summary>
public sealed class LocalSessionIssuer(byte[] key, LocalAuthOptions options, IClock clock)
{
    public const string Issuer = "spms-local";
    public const string Audience = "spms";
    /// <summary>Set while a temporary password is in use: the token then only reaches /me and /auth/change-password.</summary>
    public const string PasswordChangeClaim = "spms_pwd_change";
    /// <summary>Issue time in milliseconds: a password change in the same second as a sign-in must still end that session.</summary>
    public const string IssuedAtMsClaim = "spms_iat_ms";

    public LocalSession Issue(string subject, string displayName, bool mustChangePassword)
    {
        var now = clock.UtcNow;
        var expires = now.AddHours(mustChangePassword ? Math.Min(1, options.SessionHours) : options.SessionHours);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Uuid7.New().ToString()),
            new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new(IssuedAtMsClaim, now.ToUnixTimeMilliseconds().ToString(), ClaimValueTypes.Integer64),
            new("preferred_username", displayName),
        };
        if (mustChangePassword) claims.Add(new Claim(PasswordChangeClaim, "1"));
        var token = new JwtSecurityToken(Issuer, Audience, claims, now.UtcDateTime, expires.UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256));
        return new LocalSession(new JwtSecurityTokenHandler().WriteToken(token), expires, mustChangePassword);
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

    public static byte[] KeyFrom(string? configured, IHostEnvironment env)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var bytes = Convert.FromBase64String(configured);
            if (bytes.Length < 32) throw new InvalidOperationException("Auth:Local:SigningKey must be at least 32 bytes (base64).");
            return bytes;
        }
        if (!env.IsDevelopment()) throw new InvalidOperationException("Auth:Local:SigningKey is required when Auth:Local:Enabled is true.");
        // Development only: a fixed, published, worthless key, stable across restarts.
        return SHA256.HashData("spms-local-development-key"u8.ToArray());
    }
}
