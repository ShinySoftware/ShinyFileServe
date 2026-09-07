using ShinyFileServe.Core.Documents;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server;

/// <summary>Between the stored rules and the shape the browser's strength meter wants.</summary>
public static class PasswordPolicyMapping
{
    public static PasswordPolicyInfo ToInfo(PasswordRules rules) => new(
        rules.Enabled,
        Math.Max(1, rules.MinimumLength),
        rules.RequireUppercase,
        rules.RequireLowercase,
        rules.RequireNumber,
        rules.RequireSpecialCharacter,
        rules.BlockCommonPasswords,
        Math.Max(0, rules.MaximumAgeDays)
    );

    public static PasswordRules ToRules(PasswordPolicyInfo info) => new()
    {
        Enabled = info.Enabled,
        MinimumLength = Math.Clamp(info.MinimumLength, 1, 128),
        RequireUppercase = info.RequireUppercase,
        RequireLowercase = info.RequireLowercase,
        RequireNumber = info.RequireNumber,
        RequireSpecialCharacter = info.RequireSpecialCharacter,
        BlockCommonPasswords = info.BlockCommonPasswords,

        // Ten years is not a policy anybody wants, but it is a number; anything larger is somebody
        // trying to say "never", which has its own value.
        MaximumAgeDays = Math.Clamp(info.MaximumAgeDays, 0, 3650)
    };
}
