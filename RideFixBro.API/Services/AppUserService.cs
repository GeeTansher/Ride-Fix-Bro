using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RideFixBro.Data.Entities;

namespace RideFixBro.API.Services
{
	public sealed class AppUserService
	{
		private readonly RideFixBroDbContext _database;
		private readonly ILogger<AppUserService> _logger;

		public AppUserService(RideFixBroDbContext database, ILogger<AppUserService> logger)
		{
			_database = database;
			_logger = logger;
		}

		public async Task<User> GetOrCreateAsync(Guid supabaseUserId, string email, CancellationToken cancellationToken)
		{
			try
			{
				var user = await _database.Users.Include(row => row.Role)
					.SingleOrDefaultAsync(row => row.SupabaseUserId == supabaseUserId, cancellationToken);
				if (user is null)
				{
					var defaultRole = await _database.MasterUserRoles
						.SingleOrDefaultAsync(role => role.RoleName == "User", cancellationToken)
						?? throw new InvalidOperationException("The User role must be seeded before login.");
					user = new User
					{
						SupabaseUserId = supabaseUserId,
						Email = email,
						RoleId = defaultRole.Id,
						Role = defaultRole,
						CreatedAt = DateTime.UtcNow
					};
					_database.Users.Add(user);
					try
					{
						await _database.SaveChangesAsync(cancellationToken);
					}
					catch (DbUpdateException ex) when (ex.InnerException is SqlException sql &&
						(sql.Number == 2601 || sql.Number == 2627))
					{
						// Do first-login requests race karein toh unique index winner ko load karo.
						_database.Entry(user).State = EntityState.Detached;
						user = await _database.Users.Include(row => row.Role)
							.SingleOrDefaultAsync(row => row.SupabaseUserId == supabaseUserId, cancellationToken);
						if (user is null)
						{
							throw;
						}
					}
				}

				if (user.Role.RoleName != "User" && user.Role.RoleName != "Admin")
				{
					throw new InvalidOperationException("The account has an unsupported application role.");
				}
				return user;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Could not load the application user.");
				throw new UserStoreUnavailableException(ex);
			}
		}
	}

	public sealed class UserStoreUnavailableException : Exception
	{
		public UserStoreUnavailableException(Exception inner)
			: base("Application user storage is unavailable.", inner)
		{
		}
	}
}
