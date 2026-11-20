using System.Text.Json.Serialization;
using LabGuard.Server.Auth;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Sent by the administrator. Ids names the PCs, as in PcIdsRequest. Each map changes only the keys it lists, so PCs that differ keep
// their other choices: true restricts a policy or chooses a drive, false allows or clears it. A missing map changes nothing.
// ApplyNow asks the PCs to restart File Explorer once the policies are written; it must be in the body, true or false.
public record PcRestrictionsRequest(
    int[]? Ids,
    Dictionary<string, bool>? Policies,
    Dictionary<string, bool>? HiddenDrives,
    Dictionary<string, bool>? DisabledDrives,
    [property: JsonRequired] bool? ApplyNow);

// The Restrictions policies the administrator chose for a PC, by id, and the drive letters File Explorer hides or blocks.
public record PcRestrictionsResponse(string[] Policies, string[] HiddenDrives, string[] DisabledDrives);

public static partial class PcEndpoints
{
    // How often a patch that met another change is tried again before the server answers busy.
    private const int MaxRestrictionAttempts = 5;

    // Changes every named PC, offline ones too: the PC's service applies it from its next report. Unknown ids are left out of the count.
    // An unknown policy id or a key that is not one capital letter refuses the whole request.
    private static async Task<IResult> SetRestrictionsAsync(HttpContext http, AppDbContext db)
    {
        var request = await AuthEndpoints.ReadBodyAsync<PcRestrictionsRequest>(http.Request);
        var pcIds = ValidPcIds(request?.Ids);
        if (request?.ApplyNow is not { } applyNow || pcIds is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var policies = request.Policies ?? [];
        var hiddenDrives = request.HiddenDrives ?? [];
        var disabledDrives = request.DisabledDrives ?? [];
        if (!policies.Keys.All(PcRestrictionRules.IsPolicyId)
            || !hiddenDrives.Keys.All(PcRestrictionRules.IsDriveLetter)
            || !disabledDrives.Keys.All(PcRestrictionRules.IsDriveLetter))
        {
            return AuthEndpoints.InvalidRequest();
        }

        for (var attempt = 1; attempt <= MaxRestrictionAttempts; attempt++)
        {
            if (await TryPatchRestrictionsAsync(db, pcIds, policies, hiddenDrives, disabledDrives, applyNow, http.RequestAborted) is { } affected)
            {
                return Results.Ok(new AffectedPcsResponse(affected));
            }
        }

        return ClientEndpoints.ServerBusy();
    }

    // Reads the PCs, then writes each one only if its RestrictionsVersion is still the one read, all in one transaction. A PC another
    // request changed in between, or removed, rolls the whole patch back and returns null, so the caller runs it again on fresh rows and
    // no change is lost. Only these restriction columns are written, and the version is checked only here, so other saves of a PC (a
    // login, a rename) are never refused because of a restrictions change.
    private static async Task<int?> TryPatchRestrictionsAsync(
        AppDbContext db,
        int[] pcIds,
        Dictionary<string, bool> policies,
        Dictionary<string, bool> hiddenDrives,
        Dictionary<string, bool> disabledDrives,
        bool applyNow,
        CancellationToken ct)
    {
        var pcs = await db.Pcs
            .AsNoTracking()
            .Where(p => pcIds.Contains(p.Id))
            .Select(p => new { p.Id, p.RestrictedPolicies, p.HiddenDrives, p.DisabledDrives, p.ExplorerRestartGeneration, p.RestrictionsVersion })
            .ToListAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var pc in pcs)
        {
            var nextPolicies = PcRestrictionRules.ApplyPolicies(pc.RestrictedPolicies, policies);
            var nextHidden = PcRestrictionRules.ApplyDrives(pc.HiddenDrives, hiddenDrives);
            var nextDisabled = PcRestrictionRules.ApplyDrives(pc.DisabledDrives, disabledDrives);
            var nextGeneration = applyNow ? PcRestrictionRules.NextCount(pc.ExplorerRestartGeneration) : pc.ExplorerRestartGeneration;
            var nextVersion = PcRestrictionRules.NextCount(pc.RestrictionsVersion);
            var updated = await db.Pcs
                .Where(p => p.Id == pc.Id && p.RestrictionsVersion == pc.RestrictionsVersion)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.RestrictedPolicies, nextPolicies)
                    .SetProperty(p => p.HiddenDrives, nextHidden)
                    .SetProperty(p => p.DisabledDrives, nextDisabled)
                    .SetProperty(p => p.ExplorerRestartGeneration, nextGeneration)
                    .SetProperty(p => p.RestrictionsVersion, nextVersion), ct);
            if (updated == 0)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }
        }

        await transaction.CommitAsync(ct);
        return pcs.Count;
    }

    internal static PcRestrictionsResponse ToRestrictions(Pc pc) => new(
        PcRestrictionRules.ReadPolicies(pc.RestrictedPolicies),
        PcRestrictionRules.ReadDrives(pc.HiddenDrives),
        PcRestrictionRules.ReadDrives(pc.DisabledDrives));
}
