using Shiny.DocumentDb;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Services;

/// <summary>Raised when an operation would leave the server with no way back into its own settings.</summary>
public class ProtectedAccountException(string message) : InvalidOperationException(message);

/// <summary>Why a sign-in produced no session.</summary>
public enum AuthenticationFailure
{
    /// <summary>
    /// No such account, or the password did not match, or the account signs in without one. Reported
    /// as a single outcome on purpose: telling them apart would tell a stranger which usernames exist.
    /// </summary>
    Unknown,

    /// <summary>
    /// The credentials were right, but the account's expiry has passed. Checked after the password so
    /// only somebody who already holds the credentials learns the account is there at all.
    /// </summary>
    Expired,

    /// <summary>
    /// The credentials were right, but the account locked itself after too many failed sign-ins.
    /// Checked after the password for the same reason as <see cref="Expired"/>, and it matters more
    /// here: a wrong password against a locked account has to read exactly like a wrong password
    /// against any other, or "this account is locked" becomes a way of asking which accounts exist.
    /// </summary>
    LockedOut
}

public record AuthenticationResult(User? User, AuthenticationFailure Failure)
{
    /// <summary>
    /// The account this very attempt locked, when it locked one. Carried out of the manager only so
    /// the caller can write one activity entry naming it - what the caller is told is the same
    /// vague refusal either way.
    /// </summary>
    public User? JustLocked { get; init; }

    public static AuthenticationResult Success(User user) => new(user, AuthenticationFailure.Unknown);
    public static AuthenticationResult Failed(AuthenticationFailure failure) => new(null, failure);
}

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

    /// <summary>
    /// Creates an account with no password at all. Never an administrator, and never protected -
    /// the whole point is that anybody may become it.
    /// </summary>
    Task<User> CreateAnonymousUser(string username, string displayName, CancellationToken ct = default);

    Task SaveUser(User user, CancellationToken ct = default);

    /// <summary>
    /// Replaces the stored password and records when. Also ends any lockout: the run of guesses was
    /// against a password that no longer exists.
    /// </summary>
    Task SetPassword(string userId, string password, CancellationToken ct = default);

    Task DeleteUser(string id, CancellationToken ct = default);

    /// <summary>
    /// Records one failed proof of an account's password, and locks the account when that was one
    /// too many. Called from sign-in and from the self-service password change, so the two share one
    /// counter rather than offering a second, unmetered place to guess.
    /// </summary>
    Task RegisterFailedSignIn(User user, CancellationToken ct = default);

    /// <summary>Ends a lockout and clears the count behind it. Administrator-only above this layer.</summary>
    Task Unlock(string userId, CancellationToken ct = default);

    /// <summary>Returns the user when the credentials match an account that has not expired.</summary>
    Task<AuthenticationResult> Authenticate(string username, string password, CancellationToken ct = default);

    /// <summary>
    /// Signs in an account that carries no password. Every refusal - no such account, not an
    /// anonymous one, expired - comes back as <see cref="AuthenticationFailure.Unknown"/>, because
    /// this route is reachable by anybody and the three answers together are an account enumerator.
    /// </summary>
    Task<AuthenticationResult> AuthenticateAnonymous(string userId, CancellationToken ct = default);

    /// <summary>The accounts anybody may sign in as, for the sign-in page. Never any other account.</summary>
    Task<IReadOnlyList<User>> GetAnonymousAccounts(CancellationToken ct = default);

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
        // An account that signs in without a password never administers anything, whatever the
        // stored flags say. The save paths refuse to write that state; this makes it unreachable
        // even if a database arrived with it already in place.
        => !user.IsAnonymous && (user.IsAdmin || roles.Any(r => r.IsAdmin));

    /// <summary>
    /// What one account may do with the package feeds: everything for an administrator, otherwise
    /// the union of its groups with the account's own per-feed overrides laid over it. An account
    /// anybody can sign in as gets nothing of its own - the anonymous set in the settings covers it.
    /// </summary>
    public static PackagePermissions PackagePermissions(this User user, IReadOnlyList<Role> roles)
    {
        if (user.IsAnonymous)
            return Shared.PackagePermissions.None;

        if (user.IsAdministrator(roles))
            return PackagePermissionsExtensions.All;

        return GroupPackagePermissions(roles).Apply(user.NuGetAccess, user.NpmAccess);
    }

    /// <summary>The union of the groups alone - what an override of <see cref="PackageAccessLevel.Inherit"/> falls back to.</summary>
    public static PackagePermissions GroupPackagePermissions(IReadOnlyList<Role> roles)
        => roles.Aggregate(Shared.PackagePermissions.None, (acc, r) => acc | r.PackagePermissions);
}

public class UserManager(IDocumentStore store, IPasswordPolicy passwords, ISettingsManager settings, TimeProvider time) : IUserManager
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
            IsProtected = true,
            CreatedUtc = time.GetUtcNow(),
            PasswordChangedUtc = time.GetUtcNow()
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
            CreatedUtc = time.GetUtcNow(),
            PasswordChangedUtc = time.GetUtcNow()
        };
        await store.Insert(user, cancellationToken: ct);
        return user;
    }

    public async Task<User> CreateAnonymousUser(string username, string displayName, CancellationToken ct = default)
    {
        if (await this.FindByUsername(username, ct) is not null)
            throw new InvalidOperationException($"A user named '{username}' already exists");

        var user = new User
        {
            Id = Ids.New(),
            Username = username.Trim(),
            DisplayName = String.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(),
            IsAnonymous = true,
            CreatedUtc = time.GetUtcNow()
        };
        await store.Insert(user, cancellationToken: ct);
        return user;
    }

    public async Task SaveUser(User user, CancellationToken ct = default)
    {
        // The protected account can be renamed and re-passworded, and nothing else. Enforced here
        // rather than in the pages, so no caller can talk the server out of its own administrator.
        // An expiry is on that list: it would lock the only administrator out on a timer, with
        // nobody left who could undo it.
        if (user.IsProtected)
        {
            user.IsAdmin = true;
            user.ExpiresAtUtc = null;
            user.IsAnonymous = false;
        }

        // An account anybody may become must not carry administration with it, through the account
        // or through a group. Refused rather than quietly corrected: an operator who ticked both
        // meant something, and doing half of it silently is how a server ends up wide open.
        if (user.IsAnonymous)
        {
            if (user.IsAdmin)
            {
                throw new ProtectedAccountException(
                    $"'{user.Username}' signs in without a password, so it cannot be an administrator.");
            }

            var roles = await this.GetRolesFor(user, ct);
            if (roles.FirstOrDefault(r => r.IsAdmin) is { } adminRole)
            {
                throw new ProtectedAccountException(
                    $"'{user.Username}' signs in without a password, so it cannot be in '{adminRole.Name}', which administers this server.");
            }

            // Nothing to verify a password against any more, and a hash left behind would come back
            // the moment the account stopped being anonymous.
            user.PasswordHash = "";
            user.PasswordSalt = "";
            user.PasswordIterations = 0;
        }

        await store.Update(user, cancellationToken: ct);
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

        // Having a password and being open to anybody are the same question asked twice. Setting one
        // answers it, so the account stops being anonymous here rather than in whoever called.
        user.IsAnonymous = false;

        // Every path that sets a password comes through here - creation aside, which stamps its own -
        // so this is the one place the age has to be recorded.
        user.PasswordChangedUtc = time.GetUtcNow();

        // A lockout is a run of guesses against a password that has just stopped existing. Whoever
        // set this one is either the person themselves, who proved they knew the old one, or an
        // administrator, who is the only one who could have unlocked the account anyway.
        user.FailedSignInCount = 0;
        user.LockedOutUtc = null;

        await store.Update(user, cancellationToken: ct);
    }

    public async Task RegisterFailedSignIn(User user, CancellationToken ct = default)
    {
        // Once locked the count stops moving. It is the number the account locked on, which is what
        // an administrator is shown; letting it climb would turn "locked after ten attempts" into a
        // running total of everything anybody has tried since.
        if (user.IsLockedOut)
            return;

        user.FailedSignInCount++;

        // The account this server was set up with never locks. Recovery from a locked owner needs
        // somebody sitting at the machine to use the loopback sign-in, and plenty of these servers
        // are reached only over the network - so a stranger could put the one administrator out of
        // reach with ten wrong guesses from anywhere. That is the failure the protected flag exists
        // to prevent. The attempts are still counted and still logged, so the pressure is visible.
        var threshold = settings.Current.MaxFailedSignIns;
        if (threshold > 0 && !user.IsProtected && user.FailedSignInCount >= threshold)
            user.LockedOutUtc = time.GetUtcNow();

        await store.Update(user, cancellationToken: ct);
    }

    public async Task Unlock(string userId, CancellationToken ct = default)
    {
        var user = await store.Get<User>(userId, cancellationToken: ct)
            ?? throw new InvalidOperationException("User not found");

        user.LockedOutUtc = null;
        user.FailedSignInCount = 0;
        await store.Update(user, cancellationToken: ct);
    }

    public async Task DeleteUser(string id, CancellationToken ct = default)
    {
        if (await store.Get<User>(id, cancellationToken: ct) is { IsProtected: true } protectedUser)
            throw new ProtectedAccountException($"'{protectedUser.Username}' is this server's administrator and cannot be deleted.");

        // A token outliving its account would be a credential with nobody behind it.
        await store.Query<AccessToken>().Where(x => x.UserId == id).ExecuteDelete(ct);
        await store.Remove<User>(id, cancellationToken: ct);
    }

    public async Task<AuthenticationResult> Authenticate(string username, string password, CancellationToken ct = default)
    {
        var user = await this.FindByUsername(username, ct);

        // An anonymous account has no hash to check against, and saying so would name the accounts
        // anybody can walk into. It reads exactly like a username that does not exist.
        if (user is null || user.IsAnonymous)
            return AuthenticationResult.Failed(AuthenticationFailure.Unknown);

        if (!PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
        {
            var wasLocked = user.IsLockedOut;
            await this.RegisterFailedSignIn(user, ct);

            // The refusal is the same one a stranger gets for a username that does not exist. Only
            // the log learns that this attempt was the one that locked the account.
            return AuthenticationResult.Failed(AuthenticationFailure.Unknown) with
            {
                JustLocked = !wasLocked && user.IsLockedOut ? user : null
            };
        }

        // Both checks are deliberately after the password: an account that is locked or expired can
        // be named plainly to whoever already holds its credentials, and to nobody else. Answering
        // "locked" to a wrong password would say that the username exists, which is the one thing
        // every other refusal on this path is careful not to say.
        if (user.IsLockedOut)
            return AuthenticationResult.Failed(AuthenticationFailure.LockedOut);

        if (user.IsExpired(time.GetUtcNow()))
            return AuthenticationResult.Failed(AuthenticationFailure.Expired);

        return AuthenticationResult.Success(await this.StampLogin(user, ct));
    }

    public async Task<AuthenticationResult> AuthenticateAnonymous(string userId, CancellationToken ct = default)
    {
        var user = await store.Get<User>(userId, cancellationToken: ct);

        // One answer for all three refusals. This route is open to the whole network, so an id that
        // came back "expired" rather than "no such thing" would be an account enumerator.
        if (user is not { IsAnonymous: true } || user.IsExpired(time.GetUtcNow()))
            return AuthenticationResult.Failed(AuthenticationFailure.Unknown);

        return AuthenticationResult.Success(await this.StampLogin(user, ct));
    }

    public async Task<IReadOnlyList<User>> GetAnonymousAccounts(CancellationToken ct = default)
    {
        var all = await store.Query<User>().OrderBy(x => x.DisplayName).ToList(ct);
        var now = time.GetUtcNow();

        // An expired account would be a button that refuses everybody who pressed it.
        return all.Where(u => u.IsAnonymous && !u.IsExpired(now)).ToList();
    }

    async Task<User> StampLogin(User user, CancellationToken ct)
    {
        user.LastLoginUtc = time.GetUtcNow();

        // A sign-in that worked ends whatever run of failures came before it - the counter is about
        // consecutive guesses, and this was not one.
        user.FailedSignInCount = 0;

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
