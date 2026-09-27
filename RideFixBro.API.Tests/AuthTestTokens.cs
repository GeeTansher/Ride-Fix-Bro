using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;

namespace RideFixBro.API.Tests
{
	internal static class AuthTestTokens
	{
		public const string Issuer = "https://auth-test.supabase.co/auth/v1";
		public static readonly Guid UserId = Guid.Parse("b5d93e11-2913-4fa2-a5cd-ce80a91d55a8");
		public static readonly ECDsaSecurityKey SigningKey = new(ECDsa.Create(ECCurve.NamedCurves.nistP256))
		{
			KeyId = "local-test-key"
		};

		public static void Configure(JwtBearerOptions options)
		{
			options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
			options.Configuration.SigningKeys.Add(SigningKey);
			options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
			options.RefreshOnIssuerKeyNotFound = false;
		}

		public static string Create(Guid? userId = null, Dictionary<string, object>? overrides = null,
			string issuer = Issuer, string audience = "authenticated", DateTime? expires = null,
			SigningCredentials? credentials = null)
		{
			var id = userId ?? UserId;
			var claims = new Dictionary<string, object>
			{
				["sub"] = id.ToString(),
				["session_id"] = Guid.NewGuid().ToString(),
				["email"] = $"{id:N}@example.test",
				["role"] = "authenticated",
				["is_anonymous"] = false
			};
			if (overrides is not null)
			{
				foreach (var item in overrides)
				{
					claims[item.Key] = item.Value;
				}
			}
			return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
			{
				Issuer = issuer,
				Audience = audience,
				IssuedAt = DateTime.UtcNow.AddHours(-2),
				NotBefore = DateTime.UtcNow.AddHours(-2),
				Expires = expires ?? DateTime.UtcNow.AddHours(1),
				Claims = claims,
				SigningCredentials = credentials ?? new SigningCredentials(SigningKey, SecurityAlgorithms.EcdsaSha256)
			});
		}
	}
}
