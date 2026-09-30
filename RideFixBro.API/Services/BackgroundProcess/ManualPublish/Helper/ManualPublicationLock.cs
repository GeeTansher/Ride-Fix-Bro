using Microsoft.EntityFrameworkCore;
using RideFixBro.API.Common;
using RideFixBro.Data.Entities;

namespace RideFixBro.API.Services.BackgroundProcess.ManualPublish.Helper;

// Fixed named SQL lock sab app instances mein uploads serialize karta hai; table lock/SQL transaction nahi hai.
public sealed class ManualPublicationLock(RideFixBroDbContext database, string resource = "RideFix.ManualPublication") : IAsyncDisposable
{
    // Actual lock SQL mein hai. Ye flag sirf batata hai ki is object ne use acquire kiya tha.
    private bool _held;

    public async Task AcquireAsync(CancellationToken token)
    {
        if (!database.Database.IsSqlServer()) return; // SQLite tests use VectorDbService's in-process upload gate.
        await database.Database.OpenConnectionAsync(token);
        try
        {
            // Session = ye SQL connection, chat/login session nahi. Timeout 0 = busy ho toh wait nahi.
            using var command = database.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource=@resource,
                    @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0;
                SELECT @result;
                """;
            command.CommandTimeout = Configuration.ApiTimeouts.Seconds;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@resource";
            parameter.Value = resource;
            command.Parameters.Add(parameter);
            var result = (int)(await command.ExecuteScalarAsync(token)
                ?? throw new InvalidOperationException("Manual publication lock returned no result."));
            if (result < 0) throw new ChatInputException("A manual upload is already running. Retry afterwards.", 409);
            _held = true;
        }
        catch
        {
            await database.Database.CloseConnectionAsync();
            throw;
        }
    }

    public async Task EnsureHeldAsync(CancellationToken token)
    {
        if (!database.Database.IsSqlServer()) return;
        if (!_held) throw new InvalidOperationException("Publication lock was not acquired.");
        using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT APPLOCK_MODE(N'public', @resource, N'Session');";
        command.CommandTimeout = Configuration.ApiTimeouts.Seconds;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = resource;
        command.Parameters.Add(parameter);
        if (await command.ExecuteScalarAsync(token) is not string mode || mode != "Exclusive")
            throw new InvalidOperationException("The SQL publication lock was lost. Publication must stop.");
    }

    // Controller ka await using scope exit par call karta hai, return/exception/cancellation par bhi.
    public async ValueTask DisposeAsync()
    {
        if (!_held) return;
        try
        {
            using var command = database.Database.GetDbConnection().CreateCommand();
            command.CommandText = "EXEC sys.sp_releaseapplock @Resource=@resource, @LockOwner='Session';";
            command.CommandTimeout = Configuration.ApiTimeouts.Seconds;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@resource";
            parameter.Value = resource;
            command.Parameters.Add(parameter);
            // HTTP request cancel ho chuki ho tab bhi acquired lock ka cleanup attempt karna hai.
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        finally
        {
            _held = false;
            await database.Database.CloseConnectionAsync();
        }
    }
}
