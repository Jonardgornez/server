using LabGuard.Server.Clients;

namespace LabGuard.Server.Pcs;

// Turns a busy database on the admin PC routes into a clean 503 instead of a 500.
public sealed class PcStorageErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (Exception exception) when (ClientStorageErrorFilter.IsDatabaseBusy(exception))
        {
            return ClientEndpoints.ServerBusy();
        }
    }
}
