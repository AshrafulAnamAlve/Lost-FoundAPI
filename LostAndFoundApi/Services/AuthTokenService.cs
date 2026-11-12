using LostAndFoundApi.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace LostAndFoundApi.Services
{
    // Signs the token a user gets at login and checks it on every request that changes
    // something. Before this, "being logged in" was a flag in the browser's
    // localStorage and every endpoint trusted whatever user id the caller sent, so
    // anyone could delete or close anybody's report with one HTTP request.
    public class AuthTokenService
    {
        public const string Issuer = "LostAndFoundApi";
        public const string Audience = "LostAndFoundUI";
        private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

        private readonly SymmetricSecurityKey key;

        public AuthTokenService(IConfiguration configuration)
        {
            key = new SymmetricSecurityKey(ResolveKey(configuration));
        }

        // Jwt:Key when it is configured. Otherwise the key is derived from the database
        // connection string, which is already a secret that exists only where the app
        // runs - so a deployment works without one more setting to remember, and the
        // key stays stable across restarts (a random one would sign everyone out on
        // every app-pool recycle).
        private static byte[] ResolveKey(IConfiguration configuration)
        {
            var configured = configuration["Jwt:Key"];
            if (!string.IsNullOrWhiteSpace(configured) && configured.Length >= 32)
                return Encoding.UTF8.GetBytes(configured);

            var seed = configuration.GetConnectionString("LAFConntection") ?? "lost-and-found-development";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("LostAndFound.Jwt.v1"));
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(seed));
        }

        public TokenValidationParameters ValidationParameters => new()
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = ClaimTypes.Name,
        };

        public string CreateToken(Register user)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.id.ToString()),
                new Claim(ClaimTypes.Name, $"{user.FirstName} {user.LastName}".Trim()),
            };

            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = Audience,
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.Add(Lifetime),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            });
        }
    }

    // Passwords were stored and compared as plain text. New and changed passwords are
    // hashed with ASP.NET Core Identity's PBKDF2 hasher; an account that still holds a
    // plain-text password is upgraded the first time its owner signs in, so nobody has
    // to reset anything.
    public static class PasswordSecurity
    {
        private static readonly PasswordHasher<Register> Hasher = new();

        // Identity v3 hashes are base64 of a 0x01 marker byte plus PRF/iteration
        // headers, which always encodes to this prefix.
        public static bool IsHashed(string? stored) =>
            !string.IsNullOrEmpty(stored) && stored.Length > 60 && stored.StartsWith("AQAAAA", StringComparison.Ordinal);

        public static string Hash(Register user, string password) => Hasher.HashPassword(user, password);

        /// <summary>Checks a password; sets <paramref name="upgraded"/> when the stored value should be replaced.</summary>
        public static bool Verify(Register user, string provided, out string? upgraded)
        {
            upgraded = null;
            if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(user.Password)) return false;

            if (!IsHashed(user.Password))
            {
                // Ordinal: the old comparison ran in SQL Server under a case-insensitive
                // collation, so "PASSWORD" opened an account whose password was "password".
                if (!string.Equals(user.Password, provided, StringComparison.Ordinal)) return false;
                upgraded = Hash(user, provided);
                return true;
            }

            var result = Hasher.VerifyHashedPassword(user, user.Password, provided);
            if (result == PasswordVerificationResult.SuccessRehashNeeded) upgraded = Hash(user, provided);
            return result != PasswordVerificationResult.Failed;
        }
    }

    public static class ClaimsPrincipalExtensions
    {
        /// <summary>The signed-in user's id from their token, or 0 when there is none.</summary>
        public static int UserId(this ClaimsPrincipal user) =>
            int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
    }
}
