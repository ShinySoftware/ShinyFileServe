using ShinyFileServe.Core.Documents;

namespace ShinyFileServe.Core.Services;

public record PasswordValidation(bool Ok, IReadOnlyList<string> Failures)
{
    public static PasswordValidation Pass { get; } = new(true, []);

    /// <summary>One sentence a person can act on, rather than a list they have to assemble.</summary>
    public string Message => this.Failures.Count switch
    {
        0 => "",
        1 => $"The password must {this.Failures[0]}.",
        _ => $"The password must {String.Join(", ", this.Failures.Take(this.Failures.Count - 1))} and {this.Failures[^1]}."
    };
}

/// <summary>
/// The server's own check on a password. The strength meter in the browser is an affordance -
/// it tells someone how they are doing while they type - and this is the rule, applied to every
/// path that sets a password whether a browser was involved or not.
/// </summary>
public interface IPasswordPolicy
{
    PasswordRules Current { get; }

    /// <param name="userInputs">
    /// Things this particular password must not simply repeat - a username, a display name. Checked
    /// case-insensitively and only when the rules are on.
    /// </param>
    PasswordValidation Validate(string? password, IEnumerable<string>? userInputs = null);

    /// <summary>
    /// When this account's password stops being acceptable, or null when it never does - because
    /// the server expires nothing, or because the account has no password to expire.
    /// </summary>
    DateTimeOffset? ExpiresAt(User user);

    /// <summary>
    /// True when the person may sign in but may do nothing else until they set a new password. One
    /// place answers this so the gate, the DTO and the profile screen cannot drift apart.
    /// </summary>
    bool MustChangePassword(User user);
}

public class PasswordPolicy(ISettingsManager settings, TimeProvider time) : IPasswordPolicy
{
    /// <summary>
    /// The passwords that turn up first in every credential-stuffing list. Deliberately short: a
    /// real breach corpus is megabytes and belongs behind an API, and the first few dozen are what
    /// actually get tried against a small server on someone's network.
    /// </summary>
    static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "123456", "123456789", "12345678", "1234567890", "12345", "1234567", "password", "password1",
        "password123", "qwerty", "qwerty123", "qwertyuiop", "abc123", "111111", "123123", "1234",
        "iloveyou", "000000", "admin", "administrator", "welcome", "welcome1", "monkey", "dragon",
        "letmein", "login", "princess", "sunshine", "master", "hello", "freedom", "whatever",
        "trustno1", "starwars", "football", "baseball", "shadow", "michael", "superman", "batman",
        "passw0rd", "p@ssw0rd", "changeme", "secret", "root", "toor", "test", "guest", "default",
        "fileserve"
    };

    const string Special = "!@#$%^&*()_+-=[]{};':\",.<>/?\\|`~";

    public PasswordRules Current => settings.Current.PasswordRules;

    public PasswordValidation Validate(string? password, IEnumerable<string>? userInputs = null)
    {
        // Not a rule anyone chooses - a blank password is not a password.
        if (String.IsNullOrEmpty(password))
            return new PasswordValidation(false, ["not be blank"]);

        var rules = this.Current;
        if (!rules.Enabled)
            return PasswordValidation.Pass;

        var failures = new List<string>();

        var minimum = Math.Max(1, rules.MinimumLength);
        if (password.Length < minimum)
            failures.Add($"be at least {minimum} characters");

        if (rules.RequireUppercase && !password.Any(Char.IsUpper))
            failures.Add("include a capital letter");

        if (rules.RequireLowercase && !password.Any(Char.IsLower))
            failures.Add("include a lower-case letter");

        if (rules.RequireNumber && !password.Any(Char.IsDigit))
            failures.Add("include a number");

        if (rules.RequireSpecialCharacter && !password.Any(Special.Contains))
            failures.Add("include a symbol");

        if (rules.BlockCommonPasswords && Common.Contains(password))
            failures.Add("not be one of the most commonly guessed passwords");

        // A password that is just the username is the same class of mistake as "password", and no
        // length rule catches it.
        if (userInputs is not null)
        {
            foreach (var input in userInputs)
            {
                if (input is { Length: >= 3 } && String.Equals(input, password, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add("not repeat your name or username");
                    break;
                }
            }
        }

        return failures.Count == 0 ? PasswordValidation.Pass : new PasswordValidation(false, failures);
    }

    public DateTimeOffset? ExpiresAt(User user)
    {
        // An account that signs in without a password has nothing to expire, and telling one to
        // change a password it does not have would be a door with no handle on either side.
        if (user.IsAnonymous)
            return null;

        var days = this.Current.MaximumAgeDays;
        return days <= 0 ? null : user.PasswordSetUtc.AddDays(days);
    }

    public bool MustChangePassword(User user)
        => this.ExpiresAt(user) is { } expiry && expiry <= time.GetUtcNow();
}
