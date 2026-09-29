using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RideFixBro.API.Models;
using RideFixBro.Data.Entities;

namespace RideFixBro.API.Services
{
	public class GarageService(RideFixBroDbContext database)
    {
		private readonly RideFixBroDbContext _database = database;

        public Task<List<BikeResponse>> GetCatalogAsync(CancellationToken cancellationToken)
		{
			return _database.MasterBikes.AsNoTracking()
				.OrderBy(bike => bike.Make).ThenBy(bike => bike.Model).ThenBy(bike => bike.Year)
				.Select(bike => new BikeResponse(bike.Id, bike.Make, bike.Model, bike.Year))
				.ToListAsync(cancellationToken);
		}

		public Task<List<GarageBikeResponse>> GetGarageAsync(int userId, CancellationToken cancellationToken)
		{
			return _database.UserBikes.AsNoTracking().Where(bike => bike.UserId == userId)
				.OrderBy(bike => bike.Id)
				.Select(bike => new GarageBikeResponse(
					bike.Id, bike.BikeId, bike.Bike.Make, bike.Bike.Model, bike.Bike.Year, bike.CreatedAt))
				.ToListAsync(cancellationToken);
		}

		public async Task<(GarageBikeResponse? Bike, bool Created)> AddAsync(
			int userId, int bikeId, CancellationToken cancellationToken)
		{
			var existing = await FindAsync(userId, bikeId, cancellationToken);
			if (existing is not null)
			{
				return (ToResponse(existing), false);
			}

			var catalogBike = await _database.MasterBikes
				.SingleOrDefaultAsync(bike => bike.Id == bikeId, cancellationToken);
			if (catalogBike is null)
			{
				return (null, false);
			}

			var entry = new UserBike
			{
				UserId = userId,
				BikeId = catalogBike.Id,
				Bike = catalogBike,
				CreatedAt = DateTime.UtcNow
			};
			_database.UserBikes.Add(entry);
			try
			{
				await _database.SaveChangesAsync(cancellationToken);
				return (ToResponse(entry), true);
			}
			catch (DbUpdateException ex) when (ex.InnerException is SqlException sql &&
				(sql.Number == 2601 || sql.Number == 2627))
			{
				// Do add requests ek saath aayein toh DB unique index duplicate rokega.
				_database.Entry(entry).State = EntityState.Detached;
				existing = await FindAsync(userId, bikeId, cancellationToken);
				if (existing is null)
				{
					throw;
				}
				return (ToResponse(existing), false);
			}
		}

		private Task<UserBike?> FindAsync(int userId, int bikeId, CancellationToken cancellationToken)
		{
			return _database.UserBikes.AsNoTracking().Include(bike => bike.Bike)
				.SingleOrDefaultAsync(bike => bike.UserId == userId && bike.BikeId == bikeId, cancellationToken);
		}

		private static GarageBikeResponse ToResponse(UserBike bike)
		{
			return new GarageBikeResponse(
				bike.Id, bike.BikeId, bike.Bike.Make, bike.Bike.Model, bike.Bike.Year, bike.CreatedAt);
		}
	}
}
