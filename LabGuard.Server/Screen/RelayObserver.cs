namespace LabGuard.Server.Screen;

// Watches a relay that runs without being waited for, so a failure in it is logged by type instead of going unseen.
public static class RelayObserver
{
    // Never throws. Only the exception type is logged, never a frame or a message.
    public static async Task ObserveAsync(Task relay, ILogger logger)
    {
        try
        {
            await relay;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Couldn't relay a live view frame: {ExceptionType}.", exception.GetType().Name);
        }
    }
}
