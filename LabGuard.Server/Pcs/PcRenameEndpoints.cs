using System.Security.Claims;
using LabGuard.Server.Auth;
using LabGuard.Server.ClientUpdates;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Sent by the administrator to rename a PC. Only the number is chosen; the "PC-" prefix is fixed.
public record PcRenameRequest(int? Number);
public record PcRenameResponse(string Name);

// The administrator renames a PC by giving it a new number. Past sessions and logs keep the name they were stored with.
public static partial class PcEndpoints
{
    private static readonly string RenameNumberMessage =
        $"Number must be a whole number from {PcNaming.MinNumber} to {PcNaming.MaxNumber}.";

    private static async Task<IResult> RenameAsync(
        int id,
        HttpContext http,
        ClaimsPrincipal user,
        AppDbContext db,
        TimeProvider clock,
        ILoggerFactory loggerFactory)
    {
        var request = await AuthEndpoints.ReadBodyAsync<PcRenameRequest>(http.Request);
        if (request is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (request.Number is not { } number || !PcNaming.IsValidNumber(number))
        {
            return AuthEndpoints.BadRequest(RenameNumberMessage);
        }

        var name = PcNaming.NameFor(number);
        var ct = http.RequestAborted;
        var pendingSince = PcRegistrationRequest.PendingSince(clock.GetUtcNow());
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var pc = await db.Pcs.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pc is null)
        {
            return PcNotFound();
        }

        var oldName = pc.Name;
        if (oldName == name)
        {
            return Results.Ok(new PcRenameResponse(name));
        }

        // Pc names compare ignoring case in the database. A waiting registration request keeps the name it was promised.
        var isTaken = await db.Pcs.AnyAsync(p => p.Id != id && p.Name == name, ct)
            || await db.PcRegistrationRequests.AnyAsync(
                r => r.Status == RegistrationRequestStatus.Pending
                    && r.RequestedAtUtc >= pendingSince
                    && EF.Functions.Collate(r.Name, "NOCASE") == name,
                ct);
        if (isTaken)
        {
            return NameInUse(name);
        }

        pc.Name = name;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (ClientEndpoints.IsUniqueViolation(exception))
        {
            // Another PC took the name between the check above and this write.
            return NameInUse(name);
        }

        await transaction.CommitAsync(ct);

        loggerFactory.CreateLogger(nameof(PcEndpoints)).LogInformation(
            "PC {PcId} renamed from {OldName} to {NewName} by {Administrator}.",
            id,
            oldName,
            name,
            ClientUpdateEndpoints.AdministratorName(user));
        return Results.Ok(new PcRenameResponse(name));
    }

    private static IResult NameInUse(string name) => ConflictResponse($"{name} is already used by another PC.");
}
