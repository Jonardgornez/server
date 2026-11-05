using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Requests;

// No PC can send requests yet, so development gets a couple of sample ones to make the page usable.
public static class LoginRequestSeeder
{
    public static async Task SeedAsync(
        AppDbContext db,
        IConfiguration configuration,
        IHostEnvironment environment,
        TimeProvider clock,
        ILogger logger)
    {
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("Seed:SampleLoginRequests"))
        {
            return;
        }

        if (await db.LoginRequests.AnyAsync(r => r.Status == LoginRequestStatus.Pending))
        {
            return;
        }

        var now = clock.GetUtcNow();
        db.LoginRequests.AddRange(
            new LoginRequest { PcName = "PC-11", RequestedAtUtc = now.AddMinutes(-1), Status = LoginRequestStatus.Pending },
            new LoginRequest { PcName = "PC-03", RequestedAtUtc = now.AddMinutes(-3), Status = LoginRequestStatus.Pending });
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded 2 sample pending login requests.");
    }
}
