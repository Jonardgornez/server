using LabGuard.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Auth;

public static class AdminSeeder
{
    public static async Task SeedAsync(
        AppDbContext db,
        IConfiguration configuration,
        IHostEnvironment environment,
        IPasswordHasher<AdminAccount> hasher,
        ILogger logger)
    {
        if (await db.AdminAccounts.AnyAsync())
        {
            return;
        }

        if (!environment.IsDevelopment())
        {
            logger.LogWarning("No admin account exists and seeding is disabled outside Development. Nobody can sign in.");
            return;
        }

        var username = configuration["Seed:AdminUsername"];
        var password = configuration["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            logger.LogWarning("No admin account exists and Seed:AdminUsername / Seed:AdminPassword are not configured.");
            return;
        }

        var account = new AdminAccount { Username = username.Trim() };
        account.PasswordHash = hasher.HashPassword(account, password);
        db.AdminAccounts.Add(account);
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded development admin account '{Username}'.", account.Username);
    }
}
