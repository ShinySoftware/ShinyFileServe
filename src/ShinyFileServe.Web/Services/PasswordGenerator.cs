using System.Security.Cryptography;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Web.Services;

/// <summary>
/// Makes a password that passes this server's rules whatever they are set to: one of every kind of
/// character, at least as long as the rules ask. Drawn from the browser's crypto source.
/// </summary>
public static class PasswordGenerator
{
    // No 0/O or 1/l/I: a generated password is read off one screen and typed into another.
    const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    const string Lower = "abcdefghijkmnopqrstuvwxyz";
    const string Digits = "23456789";

    // Symbols the server counts, minus the ones that go wrong pasted into a chat, an email or a
    // shell: quotes, backslash, backtick and angle brackets.
    const string Symbols = "!@#$%^&*-_=+?";

    /// <summary>The length used when the rules ask for less, or for nothing.</summary>
    public const int DefaultLength = 20;

    public static string Create(PasswordPolicyInfo policy)
    {
        var length = Math.Max(DefaultLength, policy.Enabled ? policy.MinimumLength : 0);
        var chars = RandomNumberGenerator.GetItems<char>(Upper + Lower + Digits + Symbols, length);

        // One of each kind forced in, then shuffled into random places, so every rule is met
        // whatever the draw produced without the first four characters giving the game away.
        string[] kinds = [Upper, Lower, Digits, Symbols];
        for (var i = 0; i < kinds.Length; i++)
            chars[i] = kinds[i][RandomNumberGenerator.GetInt32(kinds[i].Length)];

        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }
}
