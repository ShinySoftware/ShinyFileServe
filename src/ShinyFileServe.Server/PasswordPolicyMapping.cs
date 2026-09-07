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
        rules.BlockCommonPasswords
    );

    public static PasswordRules ToRules(PasswordPolicyInfo info) => new()
    {
        Enabled = info.Enabled,
        MinimumLength = Math.Clamp(info.MinimumLength, 1, 128),
        RequireUppercase = info.RequireUppercase,
        RequireLowercase = info.RequireLowercase,
        RequireNumber = info.RequireNumber,
        RequireSpecialCharacter = info.RequireSpecialCharacter,
        BlockCommonPasswords = info.BlockCommonPasswords
    };
}
