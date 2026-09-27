using Microsoft.AspNetCore.Authentication.JwtBearer;
using RideFixBro.API.Services;
using System.Security.Claims;

namespace RideFixBro.API.Authentication
{
	public sealed class SupabaseTokenEvents(AppUserService users) : JwtBearerEvents
	{
		private readonly AppUserService _users = users;

        public override async Task TokenValidated(TokenValidatedContext context)
		{
			var principal = context.Principal!;
			var email = principal.FindFirstValue("email");
			if (!Guid.TryParse(principal.FindFirstValue("sub"), out var subject) || subject == Guid.Empty ||
				!Guid.TryParse(principal.FindFirstValue("session_id"), out var session) || session == Guid.Empty ||
				principal.FindFirstValue("role") != "authenticated" ||
				!string.Equals(principal.FindFirstValue("is_anonymous"), "false", StringComparison.OrdinalIgnoreCase) ||
				string.IsNullOrWhiteSpace(email) || email.Length > 255)
			{
				context.Fail("A valid non-anonymous Supabase user session is required.");
				return;
			}

			var user = await _users.GetOrCreateAsync(subject, email, context.HttpContext.RequestAborted);
			// Sirf verified identity aur SQL role rakho; incoming Admin/custom claims trust mat karo.
			var claims = new[]
			{
				new Claim("sub", subject.ToString()),
				new Claim("email", user.Email),
				new Claim("app_user_id", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
				new Claim(ClaimTypes.Role, user.Role.RoleName)
			};
			context.Principal = new ClaimsPrincipal(
				new ClaimsIdentity(claims, context.Scheme.Name, "email", ClaimTypes.Role));
		}

		public override async Task Challenge(JwtBearerChallengeContext context)
		{
			context.HandleResponse();
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			context.Response.Headers.WWWAuthenticate = "Bearer";
			await context.Response.WriteAsJsonAsync(new
			{
				Error = "Bhai, pehle Google se sign in kar. Session expire hua ho toh dobara login kar."
			}, context.HttpContext.RequestAborted);
		}

		public override Task Forbidden(ForbiddenContext context)
		{
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return context.Response.WriteAsJsonAsync(new
			{
				Error = "Bhai, is action ka permission tere account par nahi hai."
			}, context.HttpContext.RequestAborted);
		}
	}
}
