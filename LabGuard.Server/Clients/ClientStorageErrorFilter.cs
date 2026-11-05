using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

// Thrown when the login gate can't be entered in time.
public sealed class ServerBusyException : Exception;

// Turns the few expected storage failures on the PC channel into clean JSON answers. Everything else still surfaces as a 500.
public sealed class ClientStorageErrorFilter : IEndpointFilter
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The PC was removed while its request was running.
            return ClientEndpoints.NotRegistered();
        }
        catch (ServerBusyException)
        {
            return ClientEndpoints.ServerBusy();
        }
        catch (Exception ex) when (IsDatabaseBusy(ex))
        {
            return ClientEndpoints.ServerBusy();
        }
    }

    public static bool IsDatabaseBusy(Exception exception)
    {
        var sqlite = exception as SqliteException ?? (exception as DbUpdateException)?.InnerException as SqliteException;
        // The low byte is the primary code; the rest is the extended code.
        return sqlite is not null && (sqlite.SqliteErrorCode & 0xFF) is SqliteBusy or SqliteLocked;
    }
}
