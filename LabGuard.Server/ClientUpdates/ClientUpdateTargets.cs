using System.Linq.Expressions;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.ClientUpdates;

// The pending update of each PC: its target version and what it has reported about that update.
public static class ClientUpdateTargets
{
    // Clears the pending update of every PC matching the filter, in one statement.
    public static Task<int> ClearWhereAsync(AppDbContext db, Expression<Func<Pc, bool>> match, CancellationToken cancellationToken) =>
        db.Pcs
            .Where(match)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.ClientUpdateTargetVersion, (string?)null)
                .SetProperty(p => p.ClientUpdateState, (string?)null)
                .SetProperty(p => p.ClientUpdateError, (string?)null)
                .SetProperty(p => p.ClientUpdateStateAtUtc, (DateTimeOffset?)null), cancellationToken);
}
