using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Data;
using LabGuard.Server.Requests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LoginRequest = LabGuard.Server.Data.LoginRequest;

namespace LabGuard.Server.Tests;

public class LoginRequestEndpointTests
{
    private static readonly object TimeLimit60 = new { mode = "time-limit", minutes = 60 };
    private static readonly object OpenTime = new { mode = "open-time" };

    private static async Task<HttpClient> SignedInAsync(TestApp app)
    {
        var client = app.CreateBrowser();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = TestApp.Username, password = TestApp.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> SendRawAsync(HttpClient client, string path, string body, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, int id, object time) =>
        client.PostAsJsonAsync($"/api/login-requests/{id}/approve", new { time });

    private static Task<HttpResponseMessage> DenyAsync(HttpClient client, int id) =>
        client.PostAsJsonAsync($"/api/login-requests/{id}/deny", new { });

    private static Task<HttpResponseMessage> DenyAllAsync(HttpClient client, params int[] ids) =>
        client.PostAsJsonAsync("/api/login-requests/deny-all", new { ids });

    // Approval needs a PC that is registered and online, so each request gets its own.
    private static async Task<int> AddOnlinePcAsync(TestApp app)
    {
        var now = app.Time.GetUtcNow();
        return await app.WithDbAsync(async db =>
        {
            var pc = new Pc
            {
                Name = Guid.NewGuid().ToString("N")[..20],
                TokenHash = Guid.NewGuid().ToString("N"),
                RegisteredAtUtc = now,
                LastSeenUtc = now,
            };
            db.Pcs.Add(pc);
            await db.SaveChangesAsync();
            return pc.Id;
        });
    }

    private static async Task<int> AddAsync(
        TestApp app,
        string pcName = "PC-01",
        TimeSpan? requestedAgo = null,
        LoginRequestStatus status = LoginRequestStatus.Pending,
        int? approvedMinutes = null,
        DateTimeOffset? decidedAt = null)
    {
        var request = new LoginRequest
        {
            PcName = pcName,
            PcId = await AddOnlinePcAsync(app),
            RequestedAtUtc = app.Time.GetUtcNow() - (requestedAgo ?? TimeSpan.FromMinutes(1)),
            Status = status,
            ApprovedMinutes = approvedMinutes,
            DecidedAtUtc = status == LoginRequestStatus.Pending ? null : decidedAt ?? app.Time.GetUtcNow(),
        };
        return await app.WithDbAsync(async db =>
        {
            db.LoginRequests.Add(request);
            await db.SaveChangesAsync();
            return request.Id;
        });
    }

    private static Task<LoginRequest> StoredAsync(TestApp app, int id) =>
        app.WithDbAsync(db => db.LoginRequests.AsNoTracking().SingleAsync(r => r.Id == id));

    private static int[] Ids(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToArray();

    private static async Task AssertBadRequestAsync(HttpResponseMessage response, string? message = null)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await JsonOf(response)).GetProperty("error").GetString();
        Assert.False(string.IsNullOrEmpty(error));
        if (message is not null)
        {
            Assert.Equal(message, error);
        }
    }

    // Authentication

    [Theory]
    [InlineData("GET", "/api/login-requests")]
    [InlineData("POST", "/api/login-requests/1/approve")]
    [InlineData("POST", "/api/login-requests/1/deny")]
    [InlineData("POST", "/api/login-requests/deny-all")]
    public async Task Endpoints_WithoutCookie_Return401(string method, string path)
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task CollectionRoot_HasNoWriteEndpoint(string method)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/login-requests")
        {
            Content = new StringContent("""{"pcName":"PC-99"}""", Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);
        Assert.Equal(0, await app.WithDbAsync(db => db.LoginRequests.CountAsync()));
    }

    // List

    [Fact]
    public async Task List_StartsEmpty()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await client.GetAsync("/api/login-requests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(0, body.GetProperty("pending").GetArrayLength());
        Assert.Equal(0, body.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public async Task List_PendingIsOldestFirstWithTiesById()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var newest = await AddAsync(app, requestedAgo: TimeSpan.FromMinutes(1));
        var oldest = await AddAsync(app, requestedAgo: TimeSpan.FromMinutes(9));
        var tieFirst = await AddAsync(app, requestedAgo: TimeSpan.FromMinutes(5));
        var tieSecond = await AddAsync(app, requestedAgo: TimeSpan.FromMinutes(5));

        var body = await JsonOf(await client.GetAsync("/api/login-requests"));

        Assert.Equal([oldest, tieFirst, tieSecond, newest], Ids(body.GetProperty("pending")));
        Assert.Equal(0, body.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public async Task List_HistoryIsMostRecentlyDecidedFirstAndLimited()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var now = app.Time.GetUtcNow();
        var pending = await AddAsync(app);
        var older = await AddAsync(app, status: LoginRequestStatus.Denied, decidedAt: now.AddMinutes(-10));
        var newer = await AddAsync(app, status: LoginRequestStatus.Approved, decidedAt: now.AddMinutes(-2));

        var body = await JsonOf(await client.GetAsync("/api/login-requests"));

        Assert.Equal([newer, older], Ids(body.GetProperty("history")));
        Assert.Equal([pending], Ids(body.GetProperty("pending")));

        for (var i = 0; i < LoginRequestEndpoints.MaxHistoryEntries + 5; i++)
        {
            await AddAsync(app, status: LoginRequestStatus.Denied, decidedAt: now.AddMinutes(-100 + i));
        }

        body = await JsonOf(await client.GetAsync("/api/login-requests"));
        var history = Ids(body.GetProperty("history"));
        Assert.Equal(LoginRequestEndpoints.MaxHistoryEntries, history.Length);
        Assert.Equal(newer, history[0]);
    }

    [Fact]
    public async Task List_PendingEntryShapeHasNoTime()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        await AddAsync(app, "PC-07", TimeSpan.FromMinutes(3));

        var entry = (await JsonOf(await client.GetAsync("/api/login-requests"))).GetProperty("pending")[0];

        Assert.Equal("PC-07", entry.GetProperty("pcName").GetString());
        Assert.Equal("pending", entry.GetProperty("status").GetString());
        Assert.False(entry.TryGetProperty("time", out _));
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("decidedAt").ValueKind);
        Assert.Equal(app.Time.GetUtcNow().AddMinutes(-3).ToUnixTimeMilliseconds(), entry.GetProperty("requestedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task List_MovesAPendingRequestTenMinutesOldToHistoryAsExpired()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var requestedAt = app.Time.GetUtcNow().AddMinutes(-10);
        var id = await AddAsync(app, requestedAgo: TimeSpan.FromMinutes(10));

        var body = await JsonOf(await client.GetAsync("/api/login-requests"));

        Assert.Equal(0, body.GetProperty("pending").GetArrayLength());
        var history = body.GetProperty("history");
        Assert.Equal(1, history.GetArrayLength());
        Assert.Equal(id, history[0].GetProperty("id").GetInt32());
        Assert.Equal("cancelled", history[0].GetProperty("status").GetString());
        Assert.Equal("expired", history[0].GetProperty("reason").GetString());
        Assert.Equal(requestedAt.AddMinutes(10).ToUnixTimeMilliseconds(), history[0].GetProperty("decidedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        Assert.Equal(LoginRequestStatus.Cancelled, (await StoredAsync(app, id)).Status);
    }

    [Fact]
    public async Task List_WhileTheDatabaseIsBusy_StillAnswers_AndExpiresOnALaterPoll()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var stale = await AddAsync(app, requestedAgo: TimeSpan.FromMinutes(10));
        var connectionString = await app.WithDbAsync(db => Task.FromResult(db.Database.GetConnectionString()!));

        // A second connection holds the write lock, so the expiry write fails with SQLITE_BUSY.
        using var blocker = new SqliteConnection(connectionString);
        await blocker.OpenAsync();
        await ExecuteAsync(blocker, "BEGIN IMMEDIATE");
        try
        {
            var busy = await client.GetAsync("/api/login-requests");

            Assert.Equal(HttpStatusCode.OK, busy.StatusCode);
        }
        finally
        {
            await ExecuteAsync(blocker, "ROLLBACK");
        }

        var body = await JsonOf(await client.GetAsync("/api/login-requests"));
        Assert.Equal(0, body.GetProperty("pending").GetArrayLength());
        Assert.Equal([stale], Ids(body.GetProperty("history")));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task List_KeepsAPendingRequestNineMinutesFiftyNineSecondsOldAsPending()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app, requestedAgo: TimeSpan.FromSeconds(599));

        var body = await JsonOf(await client.GetAsync("/api/login-requests"));

        Assert.Equal([id], Ids(body.GetProperty("pending")));
        Assert.Equal(0, body.GetProperty("history").GetArrayLength());
        Assert.Equal(LoginRequestStatus.Pending, (await StoredAsync(app, id)).Status);
    }

    // Approve

    [Fact]
    public async Task Approve_WithTimeLimit_StoresAndReturnsShape()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        app.Time.Advance(TimeSpan.FromMinutes(2));
        var id = await AddAsync(app);

        var response = await ApproveAsync(client, id, TimeLimit60);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(id, body.GetProperty("id").GetInt32());
        Assert.Equal("approved", body.GetProperty("status").GetString());
        Assert.Equal("time-limit", body.GetProperty("time").GetProperty("mode").GetString());
        Assert.Equal(60, body.GetProperty("time").GetProperty("minutes").GetInt32());
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), body.GetProperty("decidedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());

        var stored = await StoredAsync(app, id);
        Assert.Equal(LoginRequestStatus.Approved, stored.Status);
        Assert.Equal(60, stored.ApprovedMinutes);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.DecidedAtUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Approve_WithOpenTime_OmitsMinutes()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app);

        var response = await ApproveAsync(client, id, OpenTime);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var time = (await JsonOf(response)).GetProperty("time");
        Assert.Equal("open-time", time.GetProperty("mode").GetString());
        Assert.False(time.TryGetProperty("minutes", out _));
        var stored = await StoredAsync(app, id);
        Assert.Equal(LoginRequestStatus.Approved, stored.Status);
        Assert.Null(stored.ApprovedMinutes);

        var listed = (await JsonOf(await client.GetAsync("/api/login-requests"))).GetProperty("history")[0];
        Assert.Equal("open-time", listed.GetProperty("time").GetProperty("mode").GetString());
    }

    [Theory]
    [InlineData("""{"time":{"mode":"time-limit","minutes":0}}""")]
    [InlineData("""{"time":{"mode":"time-limit","minutes":1441}}""")]
    [InlineData("""{"time":{"mode":"time-limit","minutes":1.5}}""")]
    [InlineData("""{"time":{"mode":"time-limit"}}""")]
    [InlineData("""{"time":{"mode":"forever"}}""")]
    [InlineData("""{"time":{"mode":"open-time","minutes":30}}""")]
    [InlineData("""{"time":null}""")]
    [InlineData("""{}""")]
    public async Task Approve_WithInvalidTime_Returns400AndLeavesRequestPending(string body)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app);

        var response = await SendRawAsync(client, $"/api/login-requests/{id}/approve", body);

        await AssertBadRequestAsync(response);
        Assert.Equal(LoginRequestStatus.Pending, (await StoredAsync(app, id)).Status);
    }

    [Fact]
    public async Task Approve_WithoutTime_UsesRequestSpecificMessages()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app);

        await AssertBadRequestAsync(await SendRawAsync(client, $"/api/login-requests/{id}/approve", "{}"), "Choose a time limit or open time.");
        var unknownMode = await SendRawAsync(client, $"/api/login-requests/{id}/approve", """{"time":{"mode":"forever"}}""");
        Assert.DoesNotContain("default", await unknownMode.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{not json", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("""{"time":{"mode":"open-time"}}""", "text/plain")]
    public async Task Approve_WithMalformedBodyOrContentType_ReturnsInvalidRequest(string body, string contentType)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app);

        var response = await SendRawAsync(client, $"/api/login-requests/{id}/approve", body, contentType);

        await AssertBadRequestAsync(response, "Invalid request.");
        Assert.Equal(LoginRequestStatus.Pending, (await StoredAsync(app, id)).Status);
    }

    [Fact]
    public async Task Approve_MissingRequest_Returns404()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await ApproveAsync(client, 999, OpenTime);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Approve_NonNumericId_Returns404()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await SendRawAsync(client, "/api/login-requests/abc/approve", """{"time":{"mode":"open-time"}}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Deny

    [Fact]
    public async Task Deny_PendingRequest_StoresAndReturnsShape()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app);
        app.Time.Advance(TimeSpan.FromMinutes(4));

        var response = await DenyAsync(client, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal("denied", body.GetProperty("status").GetString());
        Assert.False(body.TryGetProperty("time", out _));
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), body.GetProperty("decidedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        var stored = await StoredAsync(app, id);
        Assert.Equal(LoginRequestStatus.Denied, stored.Status);
        Assert.Null(stored.ApprovedMinutes);
    }

    [Fact]
    public async Task Deny_MissingRequest_Returns404()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        Assert.Equal(HttpStatusCode.NotFound, (await DenyAsync(client, 999)).StatusCode);
    }

    // A plain cross-origin form can't send JSON, so requiring it blocks forged denials from other local sites.
    [Fact]
    public async Task Deny_WithFormBody_Returns400AndStaysPending()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app);

        var formResponse = await client.PostAsync(
            $"/api/login-requests/{id}/deny",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "1" }));
        var emptyResponse = await client.PostAsync($"/api/login-requests/{id}/deny", null);

        Assert.Equal(HttpStatusCode.BadRequest, formResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);
        var stored = await app.WithDbAsync(db => db.LoginRequests.AsNoTracking().SingleAsync(r => r.Id == id));
        Assert.Equal(LoginRequestStatus.Pending, stored.Status);
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/login-requests/deny-all")]
    public async Task UnknownCharset_Returns400(string path)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}"));
        content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=bogus");

        var response = await client.PostAsync(path, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Already decided

    [Fact]
    public async Task Approve_AlreadyApproved_Returns409AndKeepsOriginal()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app, status: LoginRequestStatus.Approved, approvedMinutes: 30);

        var response = await ApproveAsync(client, id, OpenTime);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await StoredAsync(app, id);
        Assert.Equal(LoginRequestStatus.Approved, stored.Status);
        Assert.Equal(30, stored.ApprovedMinutes);
    }

    [Fact]
    public async Task Approve_AlreadyDenied_Returns409AndKeepsDenied()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app, status: LoginRequestStatus.Denied);

        var response = await ApproveAsync(client, id, TimeLimit60);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await StoredAsync(app, id);
        Assert.Equal(LoginRequestStatus.Denied, stored.Status);
        Assert.Null(stored.ApprovedMinutes);
    }

    [Fact]
    public async Task Deny_AlreadyApproved_Returns409AndKeepsApproved()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app, status: LoginRequestStatus.Approved, approvedMinutes: 30);

        var response = await DenyAsync(client, id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await StoredAsync(app, id);
        Assert.Equal(LoginRequestStatus.Approved, stored.Status);
        Assert.Equal(30, stored.ApprovedMinutes);
    }

    [Fact]
    public async Task Deny_AlreadyDenied_Returns409AndKeepsDecisionTime()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var decidedAt = app.Time.GetUtcNow().AddMinutes(-5);
        var id = await AddAsync(app, status: LoginRequestStatus.Denied, decidedAt: decidedAt);

        var response = await DenyAsync(client, id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(decidedAt.ToUnixTimeMilliseconds(), (await StoredAsync(app, id)).DecidedAtUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ConcurrentApproveAndDeny_DecideExactlyOnce()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app);

        var responses = await Task.WhenAll(ApproveAsync(client, id, TimeLimit60), DenyAsync(client, id));

        var codes = responses.Select(r => r.StatusCode).OrderBy(c => (int)c).ToArray();
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], codes);
        var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        var stored = await StoredAsync(app, id);
        Assert.Equal((await JsonOf(winner)).GetProperty("status").GetString(), stored.Status.ToString().ToLowerInvariant());
    }

    // Deny all

    [Fact]
    public async Task DenyAll_DeniesOnlyListedPendingRequests()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var first = await AddAsync(app, requestedAgo: TimeSpan.FromMinutes(5));
        var second = await AddAsync(app, requestedAgo: TimeSpan.FromMinutes(9));
        var unlisted = await AddAsync(app);
        var approved = await AddAsync(app, status: LoginRequestStatus.Approved, approvedMinutes: 15);
        var denied = await AddAsync(app, status: LoginRequestStatus.Denied);
        app.Time.Advance(TimeSpan.FromMinutes(1));

        var response = await DenyAllAsync(client, first, second, approved, denied, 999);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = (await JsonOf(response)).GetProperty("denied");
        Assert.Equal([second, first], Ids(entries));
        Assert.All(entries.EnumerateArray(), e =>
        {
            Assert.Equal("denied", e.GetProperty("status").GetString());
            Assert.False(e.TryGetProperty("time", out _));
            Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), e.GetProperty("decidedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        });
        Assert.Equal(LoginRequestStatus.Pending, (await StoredAsync(app, unlisted)).Status);
        var stillApproved = await StoredAsync(app, approved);
        Assert.Equal(LoginRequestStatus.Approved, stillApproved.Status);
        Assert.Equal(15, stillApproved.ApprovedMinutes);
        Assert.Equal(LoginRequestStatus.Denied, (await StoredAsync(app, first)).Status);
    }

    [Fact]
    public async Task DenyAll_WithDuplicateIds_ReportsEachOnce()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app);

        var response = await DenyAllAsync(client, id, id, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([id], Ids((await JsonOf(response)).GetProperty("denied")));
    }

    [Fact]
    public async Task DenyAll_WithEmptyList_DeniesNothing()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app);

        var response = await DenyAllAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await JsonOf(response)).GetProperty("denied").GetArrayLength());
        Assert.Equal(LoginRequestStatus.Pending, (await StoredAsync(app, id)).Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"ids":null}""")]
    [InlineData("""{"ids":[1,null]}""")]
    [InlineData("""{"ids":"1"}""")]
    [InlineData("not json")]
    public async Task DenyAll_WithoutValidIds_Returns400(string body)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        await AssertBadRequestAsync(await SendRawAsync(client, "/api/login-requests/deny-all", body));
    }

    [Fact]
    public async Task DenyAll_OverLimit_Returns400AndAtLimitIsAccepted()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app);

        var tooMany = await DenyAllAsync(client, Enumerable.Range(1, LoginRequestEndpoints.MaxDenyAllIds + 1).ToArray());
        await AssertBadRequestAsync(tooMany);
        Assert.Equal(LoginRequestStatus.Pending, (await StoredAsync(app, id)).Status);

        var atLimit = await DenyAllAsync(client, Enumerable.Range(1, LoginRequestEndpoints.MaxDenyAllIds).ToArray());
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
    }

    [Fact]
    public async Task ConcurrentDenyAllAndApprove_NeverDoubleReport()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var ids = new List<int>();
        for (var i = 0; i < 20; i++)
        {
            ids.Add(await AddAsync(app, $"PC-{i:00}"));
        }

        var approvals = ids.Select(id => ApproveAsync(client, id, TimeLimit60));
        var denyAll = DenyAllAsync(client, [.. ids]);
        var responses = await Task.WhenAll(approvals.Append(denyAll));

        var approvedIds = new List<int>();
        foreach (var approval in responses.Take(ids.Count).Where(r => r.StatusCode == HttpStatusCode.OK))
        {
            approvedIds.Add((await JsonOf(approval)).GetProperty("id").GetInt32());
        }

        var deniedIds = Ids((await JsonOf(responses[^1])).GetProperty("denied"));
        Assert.Empty(approvedIds.Intersect(deniedIds));
        Assert.Equal(ids.Count, approvedIds.Count + deniedIds.Length);
        foreach (var id in ids)
        {
            var expected = approvedIds.Contains(id) ? LoginRequestStatus.Approved : LoginRequestStatus.Denied;
            Assert.Equal(expected, (await StoredAsync(app, id)).Status);
        }
    }

    [Fact]
    public async Task ConcurrentDenyAlls_ReportEachRequestOnce()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var ids = new List<int>();
        for (var i = 0; i < 20; i++)
        {
            ids.Add(await AddAsync(app, $"PC-{i:00}"));
        }

        var responses = await Task.WhenAll(DenyAllAsync(client, [.. ids]), DenyAllAsync(client, [.. ids]));

        var reported = new List<int>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            reported.AddRange(Ids((await JsonOf(response)).GetProperty("denied")));
        }

        Assert.Equal(ids.Order(), reported.Order());
    }

    // Only a real PC creates a request: the server adds no sample requests at startup, even in development.

    [Fact]
    public async Task Startup_InDevelopment_AddsNoLoginRequests()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        Assert.Empty(await app.WithDbAsync(db => db.LoginRequests.AsNoTracking().ToListAsync()));
    }
}
