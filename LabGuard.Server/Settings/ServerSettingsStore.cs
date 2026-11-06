using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Settings;

public static class ServerSettingsStore
{
    // Tracked, so a caller can change the row and save it. The first read creates the row with the defaults.
    public static async Task<ServerSettings> GetAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var settings = await db.ServerSettingsRows.FirstOrDefaultAsync(s => s.Id == ServerSettings.SingletonId, cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        settings = new ServerSettings();
        db.ServerSettingsRows.Add(settings);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return settings;
        }
        catch (DbUpdateException)
        {
            // Another request created the row between the read and the insert, so read that one.
            db.ChangeTracker.Clear();
            return await db.ServerSettingsRows.SingleAsync(s => s.Id == ServerSettings.SingletonId, cancellationToken);
        }
    }
}
