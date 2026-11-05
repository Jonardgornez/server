using LabGuard.Server.Data;
using Microsoft.AspNetCore.Identity;

namespace LabGuard.Server.Clients;

// A password hash nobody knows, made by the same hasher that checks real passwords. Verifying against it
// costs exactly what a real check costs, so an unknown username can't be told apart by timing.
public sealed class DummyLoginCredential(IPasswordHasher<UserAccount> hasher)
{
    public UserAccount Account { get; } = new();

    public string Hash { get; } = hasher.HashPassword(new UserAccount(), Guid.NewGuid().ToString("N"));
}
