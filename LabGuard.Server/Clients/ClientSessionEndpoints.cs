using System.Globalization;
using System.Text.Json.Serialization;
using LabGuard.Server.Accounts;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using LabGuard.Server.Pcs;
using LabGuard.Server.Screen;
using LabGuard.Server.Sessions;
using LabGuard.Server.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

public record PcLoginRequest(string? Username, string? Password);
public record PcLoginResponse(int SessionId, string Username, DefaultTimeResponse Time, int? SecondsRemaining);
// PowerCancelHandled is sent by a PC that has acted on a cancel of its shutdown or restart. It only matters while a cancel is recorded.
// PowerCountdownRunning is sent by a PC that is counting down a shutdown or restart it was handed. Null from an older client.
public record HeartbeatRequest(int? SessionId, bool? PowerCancelHandled, bool? PowerCountdownRunning);
// ScreenView is set while an administrator has a live view of this PC open. The PC streams only then, and an older client ignores it.
public record HeartbeatScreenView(string ViewId, string Mode);
// Notice is the administrator's on-screen notice text, or null when the notice is turned off.
// ShutdownSeconds and RestartSeconds are set only on the one heartbeat that hands the PC a requested shutdown or restart,
// and say how long it has to go down: the countdown stored with that delivery. Exactly one of them is set, so an older client
// that only reads ShutdownSeconds never shuts down for a restart.
// PowerCancelled is true while an administrator's cancel of the shutdown or restart waits for this PC to acknowledge it.
// Messages are the administrator's messages handed to the PC on this heartbeat. The field is left out when there are none.
public record HeartbeatResponse(
    PcSessionInfo? Session,
    string? EndedReason,
    bool IsLocked,
    string? Notice,
    int? ShutdownSeconds,
    int? RestartSeconds,
    bool PowerCancelled,
    HeartbeatScreenView? ScreenView,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PcMessageDelivery[]? Messages = null);

// Login, logout, going offline and heartbeat on the PC channel. The server owns the session clock; the PC only displays it.
public static partial class ClientEndpoints
{
    public const int MaxLoginsPerMinute = 20;
    public const string LoginRateLimitPolicy = "pc-login";
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LoginLockDuration = TimeSpan.FromSeconds(30);

    // Failures older than this no longer count towards a lock.
    public static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(10);

    private const int MaxLoginAttempts = 2;
    private static readonly TimeSpan GateWaitTimeout = TimeSpan.FromSeconds(3);

    // Guards only the short database steps of a login: reserving an attempt on the PC's failure counter
    // and starting the session. The slow password hash runs outside it, so logins verify in parallel.
    // Separate from the admin gate, so a lab full of logins can't delay the admin's sign-in.
    private static readonly SemaphoreSlim PcLoginGate = new(1, 1);

    // Outcome of reserving an attempt: either a response to return now, or what is needed to undo exactly this
    // attempt later. LockedUntil is the lock this attempt caused; LockAtReservation is the lock value it saw,
    // so a different value later means a lock fired in between and reset the count this attempt was part of.
    private sealed record AttemptReservation(
        IResult? Rejection,
        Pc? Pc = null,
        DateTimeOffset? LockedUntil = null,
        DateTimeOffset? LockAtReservation = null);

    private static async Task<IResult> LoginAsync(
        HttpContext http,
        AppDbContext db,
        IPasswordHasher<UserAccount> hasher,
        DummyLoginCredential dummy,
        TimeProvider clock)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var ct = http.RequestAborted;
        if (await FindPcIdAsync(db, token, ct) is not { } pcId)
        {
            return NotRegistered();
        }

        var request = await AuthEndpoints.ReadBodyAsync<PcLoginRequest>(http.Request);
        if (request?.Username is not { } rawUsername
            || request.Password is not { } password
            || string.IsNullOrWhiteSpace(rawUsername)
            || string.IsNullOrWhiteSpace(password))
        {
            return AuthEndpoints.BadRequest("Enter a username and password.");
        }

        // Every attempt is counted as a failure up front, before the hash runs. Parallel guesses therefore
        // can't slip past the counter, and a success or a disabled account gives back only this attempt's own share.
        var reservation = await InGateAsync(() => ReserveAttemptAsync(http, db, clock, pcId), ct);
        if (reservation.Rejection is { } rejection)
        {
            return rejection;
        }

        try
        {
            return await VerifyAndStartAsync(http, db, hasher, dummy, clock, reservation, rawUsername, password, ct);
        }
        catch
        {
            // A timeout or busy database must not cost the user an attempt. Best effort, and the original error still propagates.
            await TryGiveBackAttemptAsync(db, reservation);
            throw;
        }
    }

    private static async Task<IResult> VerifyAndStartAsync(
        HttpContext http,
        AppDbContext db,
        IPasswordHasher<UserAccount> hasher,
        DummyLoginCredential dummy,
        TimeProvider clock,
        AttemptReservation reservation,
        string rawUsername,
        string password,
        CancellationToken ct)
    {
        var username = rawUsername.Trim();
        // Over-long values can't match anything, so they skip the lookup and verify against the dummy hash.
        var account = username.Length <= AccountEndpoints.MaxUsernameLength && password.Length <= AccountEndpoints.MaxPasswordLength
            ? await db.UserAccounts.FirstOrDefaultAsync(u => u.Username == username, ct)
            : null;

        // Always exactly one hash verification, so timing doesn't reveal whether the username exists.
        if (!VerifyPassword(hasher, dummy, account, password) || account is null)
        {
            return LockedResponse(reservation.LockedUntil, clock.GetUtcNow(), http) ?? WrongCredentials();
        }

        if (!account.IsActive)
        {
            await InGateAsync(() => GiveBackAttemptAsync(db, reservation, ct), ct);
            return Results.Json(new ErrorResponse("This account is disabled."), statusCode: StatusCodes.Status403Forbidden);
        }

        return await InGateAsync(() => StartSessionAsync(db, reservation, account, clock.GetUtcNow(), ct), ct);
    }

    private static async Task<T> InGateAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (!await PcLoginGate.WaitAsync(GateWaitTimeout, ct))
        {
            throw new ServerBusyException();
        }

        try
        {
            return await action();
        }
        finally
        {
            PcLoginGate.Release();
        }
    }

    private static async Task<AttemptReservation> ReserveAttemptAsync(HttpContext http, AppDbContext db, TimeProvider clock, int pcId)
    {
        var ct = http.RequestAborted;
        var pc = await db.Pcs.FirstOrDefaultAsync(p => p.Id == pcId, ct);
        if (pc is null)
        {
            return new AttemptReservation(NotRegistered());
        }

        // Refused before any counting or hashing, so a locked PC neither uses up an attempt nor reveals the credentials.
        if (pc.LockedAtUtc is not null)
        {
            return new AttemptReservation(LockedByAdministrator());
        }

        var now = clock.GetUtcNow();
        if (LockedResponse(pc.LoginLockedUntilUtc, now, http) is { } locked)
        {
            return new AttemptReservation(locked);
        }

        var lockAtReservation = pc.LoginLockedUntilUtc;
        var failures = RecentFailures(pc, now) + 1;
        DateTimeOffset? lockedUntil = null;
        pc.LastFailedLoginUtc = now;
        if (failures >= MaxFailedLogins)
        {
            lockedUntil = now + LoginLockDuration;
            pc.LoginLockedUntilUtc = lockedUntil;
            pc.FailedLoginCount = 0;
        }
        else
        {
            pc.FailedLoginCount = failures;
        }

        await db.SaveChangesAsync(ct);
        return new AttemptReservation(null, pc, lockedUntil, lockAtReservation);
    }

    private static int RecentFailures(Pc pc, DateTimeOffset now) =>
        pc.LastFailedLoginUtc is { } lastFailure && now - lastFailure <= FailureMemory ? pc.FailedLoginCount : 0;

    // Undoes only this attempt's own reservation, used by a success, a disabled account and a failed request.
    // It never resets the count or clears a lock that another attempt caused, and never goes below zero.
    // Must run inside PcLoginGate, so it reads and writes the counter without another attempt interleaving.
    // Returns false when the PC no longer exists.
    private static async Task<bool> GiveBackAttemptAsync(AppDbContext db, AttemptReservation reservation, CancellationToken ct)
    {
        var pcId = reservation.Pc!.Id;
        var current = await db.Pcs
            .Where(p => p.Id == pcId)
            .Select(p => new { p.FailedLoginCount, p.LoginLockedUntilUtc })
            .FirstOrDefaultAsync(ct);
        if (current is null)
        {
            return false;
        }

        int count;
        DateTimeOffset? lockedUntil;
        if (reservation.LockedUntil is { } ownLock)
        {
            // This attempt set the lock and reset the count. Only undo that while nothing has happened since.
            if (!SameInstant(current.LoginLockedUntilUtc, ownLock) || current.FailedLoginCount != 0)
            {
                return true;
            }

            count = MaxFailedLogins - 1;
            lockedUntil = null;
        }
        else
        {
            // A different lock value means one fired since and reset the count, so this attempt's share is already gone.
            if (!SameInstant(current.LoginLockedUntilUtc, reservation.LockAtReservation))
            {
                return true;
            }

            count = Math.Max(0, current.FailedLoginCount - 1);
            lockedUntil = current.LoginLockedUntilUtc;
        }

        await db.Pcs
            .Where(p => p.Id == pcId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.FailedLoginCount, count)
                .SetProperty(p => p.LoginLockedUntilUtc, lockedUntil), ct);
        return true;
    }

    // The database keeps millisecond precision, so a value written earlier is compared at that precision.
    private static bool SameInstant(DateTimeOffset? stored, DateTimeOffset? original) =>
        stored?.ToUnixTimeMilliseconds() == original?.ToUnixTimeMilliseconds();

    private static async Task TryGiveBackAttemptAsync(AppDbContext db, AttemptReservation reservation)
    {
        try
        {
            await InGateAsync(() => GiveBackAttemptAsync(db, reservation, CancellationToken.None), CancellationToken.None);
        }
        catch (Exception)
        {
            // Best effort only: the original error is what the caller needs to see.
        }
    }

    private static async Task<IResult> StartSessionAsync(
        AppDbContext db,
        AttemptReservation reservation,
        UserAccount account,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var pc = reservation.Pc!;
        for (var attempt = 0; attempt < MaxLoginAttempts; attempt++)
        {
            var session = new PcSession
            {
                PcId = pc.Id,
                PcName = pc.Name,
                UserAccountId = account.Id,
                Username = account.Username,
                StartedAtUtc = now,
                ExpiresAtUtc = account.DefaultTimeMinutes is { } minutes ? now.AddMinutes(minutes) : null,
                DefaultTimeMinutes = account.DefaultTimeMinutes,
            };

            // Ending the old session and creating the new one succeed or fail together.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                // Also confirms the PC and the (still enabled) account weren't removed since they were read.
                // A success undoes only its own reservation; failures by others stay counted.
                if (!await GiveBackAttemptAsync(db, reservation, ct))
                {
                    return NotRegistered();
                }

                // An admin lock that landed after the password check must not get a session that runs unpaused.
                // Committed, so the attempt stays given back.
                if (await db.Pcs.AnyAsync(p => p.Id == pc.Id && p.LockedAtUtc != null, ct))
                {
                    await transaction.CommitAsync(ct);
                    return LockedByAdministrator();
                }

                var accountUpdated = await db.UserAccounts
                    .Where(u => u.Id == account.Id && u.IsActive)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.LastLoginUtc, now), ct);
                if (accountUpdated == 0)
                {
                    return WrongCredentials();
                }

                await SessionLifecycle.EndExpiredForPcAsync(db, pc.Id, now, ct);
                await SessionLifecycle.EndForPcAsync(db, pc.Id, SessionLifecycle.Replaced, now, ct);

                db.PcSessions.Add(session);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Another session appeared for this PC in between; start over once.
                db.Entry(session).State = EntityState.Detached;
                continue;
            }
            catch (Exception ex) when (ClientStorageErrorFilter.IsDatabaseBusy(ex) && attempt < MaxLoginAttempts - 1)
            {
                // A heartbeat can commit between this transaction's read and its first write; start over once.
                db.ChangeTracker.Clear();
                continue;
            }

            return Results.Ok(new PcLoginResponse(
                session.Id,
                session.Username,
                SessionLifecycle.ToTime(session.DefaultTimeMinutes),
                SessionLifecycle.SecondsRemaining(session, now)));
        }

        // The password was correct, so this failure must not cost the student an attempt.
        db.ChangeTracker.Clear();
        await GiveBackAttemptAsync(db, reservation, ct);
        return Results.Json(new ErrorResponse("Couldn't start your session. Try again."), statusCode: StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var ct = http.RequestAborted;
        if (await FindPcIdAsync(db, token, ct) is not { } pcId)
        {
            return NotRegistered();
        }

        var now = clock.GetUtcNow();
        var delivery = await db.Pcs
            .Where(p => p.Id == pcId)
            .Select(p => new { p.ShutdownDeliveredAtUtc, p.PowerAction, p.PowerCountdownSeconds })
            .FirstOrDefaultAsync(ct);
        var reason = LogoutReason(delivery?.ShutdownDeliveredAtUtc, delivery?.PowerAction, delivery?.PowerCountdownSeconds, now);
        await SessionLifecycle.EndExpiredForPcAsync(db, pcId, now, ct);
        await SessionLifecycle.EndForPcAsync(db, pcId, reason, now, ct);
        return Results.NoContent();
    }

    // A PC the administrator has just shut down or restarted ends its session as an admin action, not as a plain logout.
    private static string LogoutReason(DateTimeOffset? deliveredAt, string? storedAction, int? countdownSeconds, DateTimeOffset now)
    {
        if (deliveredAt is not { } delivered || !PcShutdownRules.IsWithin(delivered, now, PcShutdownRules.LogoutWindowOf(countdownSeconds)))
        {
            return SessionLifecycle.Logout;
        }

        return PcShutdownRules.ActionOf(storedAction) == PcShutdownRules.RestartAction
            ? SessionLifecycle.AdminReboot
            : SessionLifecycle.AdminShutdown;
    }

    // The PC says it is closing. Its last heartbeat moves back to the edge of the online window, so it shows offline at once.
    // Only ever moves backward, and leaves its sessions and its lock alone. A pending shutdown or restart request is dropped too,
    // since the PC is going away and would only get it on a heartbeat that comes after it has restarted.
    private static async Task<IResult> OfflineAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var ct = http.RequestAborted;
        if (await FindPcIdAsync(db, token, ct) is not { } pcId)
        {
            return NotRegistered();
        }

        // Exactly the moment IsOnline gives up on: a PC last seen this long ago is offline.
        var offlineSince = clock.GetUtcNow() - PcEndpoints.OnlineWindow;
        await db.Pcs
            .Where(p => p.Id == pcId && p.LastSeenUtc > offlineSince)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.LastSeenUtc, offlineSince), ct);
        await db.Pcs
            .Where(p => p.Id == pcId && p.ShutdownRequestedAtUtc != null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)null), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> HeartbeatAsync(
        HttpContext http,
        AppDbContext db,
        TimeProvider clock,
        ILocalAddressSource localAddresses,
        ScreenStreamHub screens)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var tokenHash = HashToken(token);
        var now = clock.GetUtcNow();
        var address = RemoteAddress(http)?.ToString();
        var ct = http.RequestAborted;

        var known = await db.Pcs
            .AsNoTracking()
            .Where(p => p.TokenHash == tokenHash)
            .Select(p => new
            {
                p.Id,
                p.LastSeenUtc,
                IsLocked = p.LockedAtUtc != null,
                p.ShutdownRequestedAtUtc,
                p.ShutdownDeliveredAtUtc,
                p.PowerAction,
                p.PowerCountdownSeconds,
                p.PowerCancelRequestedAtUtc,
            })
            .FirstOrDefaultAsync(ct);
        if (known is null)
        {
            return NotRegistered();
        }

        var pcId = known.Id;

        // Read from the snapshot taken above, so it is the state before this heartbeat writes LastSeen.
        var wasOnline = PcEndpoints.IsOnline(known.LastSeenUtc, now);

        // A missing, empty or malformed body is still a heartbeat, just without a session id.
        var request = await AuthEndpoints.ReadBodyAsync<HeartbeatRequest>(http.Request);

        var active = await db.PcSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.PcId == pcId && s.EndedAtUtc == null, ct);

        // Only write when there is something to close, so an ordinary heartbeat is a single write.
        if (active is not null && SessionLifecycle.HasExpired(active, now))
        {
            await SessionLifecycle.EndExpiredForPcAsync(db, pcId, now, ct);
            active = null;
        }

        // An admin unlock is adopted by a PC that was online before this heartbeat, or that reports its id (a network blip).
        // A PC that was offline and doesn't know the id, such as a rebooted one, must not open itself unattended.
        if (active is { IsAdminUnlock: true } && request?.SessionId != active.Id && !wasOnline)
        {
            await SessionLifecycle.EndSessionAsync(db, active.Id, SessionLifecycle.ConnectionLost, now, ct);
            active = null;
        }

        // Written after the decision above, so a heartbeat that dies partway can't make the PC look online with the
        // unlock still open. A single conditional update, so a PC removed a moment ago can't be revived.
        var changed = await db.Pcs
            .Where(p => p.TokenHash == tokenHash)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.LastSeenUtc, now)
                .SetProperty(p => p.IpAddress, address), ct);
        if (changed == 0)
        {
            return NotRegistered();
        }

        var handOff = await HandOffPowerAsync(
            db,
            tokenHash,
            address,
            wasOnline,
            localAddresses,
            new PowerSnapshot(
                known.ShutdownRequestedAtUtc,
                known.ShutdownDeliveredAtUtc,
                known.PowerAction,
                known.PowerCountdownSeconds,
                known.PowerCancelRequestedAtUtc),
            request?.PowerCancelHandled,
            request?.PowerCountdownRunning,
            now,
            ct);

        // Only this PC's own ended sessions are ever reported, never another PC's.
        string? endedReason = null;
        if (request?.SessionId is { } sessionId && active?.Id != sessionId)
        {
            endedReason = await db.PcSessions
                .Where(s => s.Id == sessionId && s.PcId == pcId && s.EndedAtUtc != null)
                .Select(s => s.EndReason)
                .FirstOrDefaultAsync(ct);
        }

        var messages = await HandOverMessagesAsync(db, pcId, now, ct);

        var settings = await ServerSettingsStore.GetAsync(db, ct);
        var screenView = screens.CurrentFor(pcId) is { } viewer ? new HeartbeatScreenView(viewer.ViewId, viewer.Mode) : null;
        return Results.Ok(new HeartbeatResponse(
            active is null ? null : SessionLifecycle.ToInfo(active, now),
            endedReason,
            known.IsLocked,
            settings.NoticeEnabled ? settings.NoticeText : null,
            handOff.Delivery?.Action == PcShutdownRules.ShutdownAction ? handOff.Delivery.CountdownSeconds : null,
            handOff.Delivery?.Action == PcShutdownRules.RestartAction ? handOff.Delivery.CountdownSeconds : null,
            handOff.IsCancelled,
            screenView,
            messages.Length == 0 ? null : messages));
    }

    private static Task<int?> FindPcIdAsync(AppDbContext db, string token, CancellationToken ct)
    {
        var tokenHash = HashToken(token);
        return db.Pcs
            .AsNoTracking()
            .Where(p => p.TokenHash == tokenHash)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static bool VerifyPassword(IPasswordHasher<UserAccount> hasher, DummyLoginCredential dummy, UserAccount? account, string password)
    {
        var result = hasher.VerifyHashedPassword(
            account ?? dummy.Account,
            account?.PasswordHash ?? dummy.Hash,
            password);
        return account is not null && result != PasswordVerificationResult.Failed;
    }

    private static IResult LockedByAdministrator() =>
        Results.Json(new ErrorResponse("This PC is locked by the administrator."), statusCode: StatusCodes.Status403Forbidden);

    private static IResult? LockedResponse(DateTimeOffset? lockedUntilUtc, DateTimeOffset now, HttpContext http)
    {
        if (lockedUntilUtc is not { } lockedUntil || lockedUntil <= now)
        {
            return null;
        }

        var seconds = (int)Math.Ceiling((lockedUntil - now).TotalSeconds);
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(
            new RateLimitResponse($"Too many attempts. Try again in {seconds} seconds.", seconds),
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    // 403, never 401: the PC treats 401 as "this PC was removed".
    private static IResult WrongCredentials() =>
        Results.Json(new ErrorResponse("Wrong username or password."), statusCode: StatusCodes.Status403Forbidden);
}
