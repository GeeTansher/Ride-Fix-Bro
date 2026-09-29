using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using RideFixBro.API.Configuration;

namespace RideFixBro.API.Authentication
{
	public static class SupabaseAuthentication
	{
		public static IServiceCollection AddSupabaseAuthentication(this IServiceCollection services, IConfiguration config)
		{
			services.AddScoped<SupabaseTokenEvents>();
			services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
				.AddJwtBearer(options =>
				{
					// User Secrets/Azure: Supabase:ValidIssuer = https://PROJECT.supabase.co/auth/v1
					// Supabase:ValidAudience = authenticated. JWT secret/private key nahi chahiye.
					var issuer = config["Supabase:ValidIssuer"]?.TrimEnd('/');
					var audience = config["Supabase:ValidAudience"];
					if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri) ||
						uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
						!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
						uri.AbsolutePath != "/auth/v1" || audience != "authenticated")
					{
						throw new InvalidOperationException(
							"Set Supabase:ValidIssuer to your HTTPS project /auth/v1 URL and Supabase:ValidAudience to authenticated.");
					}

					options.Authority = issuer;
					options.Audience = audience;
					options.RequireHttpsMetadata = true;
					options.BackchannelTimeout = TimeSpan.FromSeconds(ApiTimeouts.Seconds);
					options.MapInboundClaims = false;
					options.SaveToken = false;
					options.IncludeErrorDetails = false;
					options.AutomaticRefreshInterval = TimeSpan.FromMinutes(10);
					options.RefreshInterval = TimeSpan.FromMinutes(5);
					options.EventsType = typeof(SupabaseTokenEvents);
					// .NET discovery/JWKS se keys refresh karega; Google ID token API access token nahi hai.
					options.TokenValidationParameters = new TokenValidationParameters
					{
						ValidateIssuer = true,
						ValidIssuer = issuer,
						ValidateAudience = true,
						ValidAudience = audience,
						ValidateIssuerSigningKey = true,
						RequireSignedTokens = true,
						RequireExpirationTime = true,
						ValidateLifetime = true,
						ClockSkew = TimeSpan.FromSeconds(30),
						ValidAlgorithms = new[] { SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256 }
					};
				});
			services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).ValidateOnStart();
			services.AddAuthorization();
			return services;
		}
	}
}
