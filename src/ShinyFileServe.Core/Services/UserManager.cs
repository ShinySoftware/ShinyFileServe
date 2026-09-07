using Shiny.DocumentDb;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Services;

/// <summary>Raised when an operation would leave the server with no way back into its own settings.</summary>
public class ProtectedAccountException(string message) : InvalidOperationException(message);

public interface IUserManager
{
    /// <summary>
    /// True until the first administrator exists. Nothing is seeded on first run: an account with
    /// a password the whole internet knows is worse than no account at all, so the first person to
    /// reach the site creates it.
    /// </summary>
    Task<bool> NeedsSetup(CancellationToken ct = default);

    /// <summary>
    /// Creates the administrators group and the first administrator in it, both protected. Refuses
    /// once any user exists, so the endpoint behind it cannot be replayed.
    /// </summary>
    Task<User> CompleteSetup(string username, string password, string displayName, CancellationToken ct = default);

    Task<IReadOnlyList<User>> GetUsers(CancellationToken ct = default);
    Task<User?> GetUser(string id, CancellationToken ct = default);
    Task<User?> FindByUsername(string username, CancellationToken ct = default);
    Task<User> CreateUser(string username, string password, string displayName, bool isAdmin = false, CancellationToken ct = default);
    Task SaveUser(User user, CancellationToken ct = default);
    Task SetPassword(string userId, string password, CancellationToken ct = default);
    Task DeleteUser(string id, CancellationToken ct = default);

    /// <summary>Returns the user when the credentials match an active account, otherwise null.</summary>
    Task<User?> Authenticate(string username, string password, CancellationToken ct = default);

    Task<IReadOnlyList<Role>> GetRoles(CancellationToken ct = default);

    /// <summary>The roles one user actually holds, in the order they are stored.</summary>
    Task<IReadOnlyList<Role>> GetRolesFor(User user, CancellationToken ct = default);
    Task<Role> CreateRole(string name, string? description = null, CancellationToken ct = default);
    Task SaveRole(Role role, CancellationToken ct = default);
    Task DeleteRole(string id, CancellationToken ct = default);
}

public static class AdminRules
{
    /// <summary>
    /// Admin is granted either directly on the account or through any group the user belongs to.
    /// Every check goes through here so the two paths can never disagree.
    /// </summary>
    public static bool IsAdministrator(this User user, IEnumerable<Role> roles)
        => user.IsAdmin || roles.Any(r => r.IsAdmin);
}

public class UserManager(IDocumentStore store, IPasswordPolicy passwords, TimeProvider time) : IUserManager
{
    public async Task<bool> NeedsSetup(CancellationToken ct = default)
        => !await store.Query<User>().Any(ct);

    public async Task<User> CompleteSetup(string username, string password, string displayName, CancellationToken ct = default)
    {
        if (!await this.NeedsSetup(ct))
            throw new InvalidOperationException("This server has already been set up.");

        if (String.IsNullOrWhiteSpace(username))
            throw new ArgumentException("A username is required.", nameof(username));

        // The rules are configurable and can be switched off entirely, so this asks the policy
        // rather than carrying a number of its own that would drift away from it.
        if (passwords.Validate(password, [username, displayName]) is { Ok: false } invalid)
            throw new ArgumentException(invalid.Message, nameof(password));

        var role = new Role
        {
            Id = Ids.New(),
            Name = "Administrators",
            Description = "Full access to every share, the activity log and the settings.",
            IsAdmin = true,
            IsProtected = true,
            CreatedUtc = time.GetUtcNow()
        };
        await store.Insert(role, cancellationToken: ct);

        var (hash, salt, iterations) = PasswordHasher.Hash(password);
        var user = new User
        {
            Id = Ids.New(),
            Username = username.Trim(),
            DisplayName = String.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(),
            PasswordHash = hash,
            PasswordSalt = salt,
            PasswordIterations = iterations,
            RoleIds = [role.Id],
            IsAdmin = true,
            IsActive = true,
            IsProtected = true,
            CreatedUtc = time.GetUtcNow()
        };
        await store.Insert(user, cancellationToken: ct);

        return user;
    }

    public Task<IReadOnlyList<User>> GetUsers(CancellationToken ct = default)
        => store.Query<User>().OrderBy(x => x.Username).ToList(ct);

    public Task<User?> GetUser(string id, CancellationToken ct = default)
        => store.Get<User>(id, cancellationToken: ct);

    public async Task<User?> FindByUsername(string username, CancellationToken ct = default)
    {
        // usernames are stored as entered but matched case-insensitively, which is what every
        // user expects from a login box - so normalize on both sides rather than in the query
        var lowered = username.Trim().ToLowerInvariant();
        var all = await store.Query<User>().ToList(ct);
        return all.FirstOrDefault(x => x.Username.ToLowerInvariant() == lowered);
    }

    public async Task<User> CreateUser(string username, string password, string displayName, bool isAdmin = false, CancellationToken ct = default)
    {
        if (await this.FindByUsername(username, ct) is not null)
            throw new InvalidOperationException($"A user named '{username}' already exists");

        if (passwords.Validate(password, [username, displayName]) is { Ok: false } invalid)
            throw new ArgumentException(invalid.Message, nameof(password));

        var (hash, salt, iterations) = PasswordHasher.Hash(password);
        var user = new User
        {
            Id = Ids.New(),
            Username = username.Trim(),
            DisplayName = String.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(),
            PasswordHash = hash,
            PasswordSalt = salt,
            PasswordIterations = iterations,
            IsAdmin = isAdmin,
            IsActive = true,
            CreatedUtc = time.GetUtcNow()
        };
        await store.Insert(user, cancellationToken: ct);
        return user;
    }

    public Task SaveUser(User user, CancellationToken ct = default)
    {
        // The protected account can be renamed and re-passworded, and nothing else. Enforced here
        // rather than in the pages, so no caller can talk the server out of its own administrator.
        if (user.IsProtected)
        {
            user.IsAdmin = true;
            user.IsActive = true;
        }
        return store.Update(user, cancellationToken: ct);
    }

    public async Task SetPassword(string userId, string password, CancellationToken ct = default)
    {
        var user = await store.Get<User>(userId, cancellationToken: ct)
            ?? throw new InvalidOperationException("User not found");

        if (passwords.Validate(password, [user.Username, user.DisplayName]) is { Ok: false } invalid)
            throw new ArgumentException(invalid.Message, nameof(password));

        var (hash, salt, iterations) = PasswordHasher.Hash(password);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.PasswordIterations = iterations;
        await store.Update(user, cancellationToken: ct);
    }

    public async Task DeleteUser(string id, CancellationToken ct = default)
    {
        if (await store.Get<User>(id, cancellationToken: ct) is { IsProtected: true } protectedUser)
            throw new ProtectedAccountException($"'{protectedUser.Username}' is this server's administrator and cannot be deleted.");

        await store.Remove<User>(id, cancellationToken: ct);
    }

    public async Task<User?> Authenticate(string username, string password, CancellationToken ct = default)
    {
        var user = await this.FindByUsername(username, ct);
        if (user is null || !user.IsActive)
            return null;

        if (!PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
            return null;

        user.LastLoginUtc = time.GetUtcNow();
        await store.Update(user, cancellationToken: ct);
        return user;
    }

    public Task<IReadOnlyList<Role>> GetRoles(CancellationToken ct = default)
        => store.Query<Role>().OrderBy(x => x.Name).ToList(ct);

    public async Task<IReadOnlyList<Role>> GetRolesFor(User user, CancellationToken ct = default)
    {
        if (user.RoleIds.Count == 0)
            return [];

        var all = await this.GetRoles(ct);
        return all.Where(r => user.RoleIds.Contains(r.Id)).ToList();
    }

    public async Task<Role> CreateRole(string name, string? description = null, CancellationToken ct = default)
    {
        var role = new Role { Id = Ids.New(), Name = name.Trim(), Description = description, CreatedUtc = time.GetUtcNow() };
        await store.Insert(role, cancellationToken: ct);
        return role;
    }

    public Task SaveRole(Role role, CancellationToken ct = default)
    {
        if (role.IsProtected)
            role.IsAdmin = true;

        return store.Update(role, cancellationToken: ct);
    }

    public async Task DeleteRole(string id, CancellationToken ct = default)
    {
        if (await store.Get<Role>(id, cancellationToken: ct) is { IsProtected: true } protectedRole)
            throw new ProtectedAccountException($"'{protectedRole.Name}' is this server's administrators group and cannot be deleted.");

        // a role that still has grants would leave orphaned rows nobody can see or clean up
        await store.Query<FolderGrant>().Where(x => x.RoleId == id).ExecuteDelete(ct);

        var users = await store.Query<User>().ToList(ct);
        foreach (var user in users.Where(u => u.RoleIds.Contains(id)))
        {
            user.RoleIds.Remove(id);
            await store.Update(user, cancellationToken: ct);
        }
        await store.Remove<Role>(id, cancellationToken: ct);
    }
}
