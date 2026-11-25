using System.Security.Cryptography;
using System.Text;
using Shiny.Net.HttpServer;

namespace ShinyFileServe.Server.Security;

/// <summary>
/// A second way to prove you own the server before the first administrator exists. The desktop app
/// proves it by being on the machine - setup is refused from anywhere else. A headless server, in a
/// container say, has nobody on its machine, so it prints a one-time code to its own log instead:
/// whoever can read that log already controls the host, which is the same proof by other means.
///
/// Disabled (no code) unless the host creates one. Only ever consulted while there are no accounts.
/// </summary>
public class SetupCode
{
    /// <summary>Sent by the setup page on every setup call - a header, so the multipart restore carries it too.</summary>
    public const string HeaderName = "X-FileServe-Setup-Code";

    // No 0/O, 1/I/L: it is read off a log and typed by hand.
    const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    readonly byte[]? expected;

    public SetupCode(string? code = null)
    {
        this.Code = code;
        this.expected = code is null ? null : Encoding.ASCII.GetBytes(Normalize(code));
    }

    /// <summary>The code as the log shows it, or null when setup is only possible on the machine itself.</summary>
    public string? Code { get; }

    public bool IsEnabled => this.expected is not null;

    /// <summary>Twelve characters from a 31-letter alphabet, in groups of four. About 59 bits.</summary>
    public static SetupCode Generate()
    {
        var chars = RandomNumberGenerator.GetItems<char>(Alphabet, 12);
        var text = $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
        return new SetupCode(text);
    }

    public bool Matches(HttpContext context)
    {
        if (this.expected is not { } want)
            return false;

        var sent = context.Request.Headers.GetFirst(HeaderName);
        if (String.IsNullOrWhiteSpace(sent))
            return false;

        var got = Encoding.ASCII.GetBytes(Normalize(sent));
        return CryptographicOperations.FixedTimeEquals(got, want);
    }

    // Case and the separators are typing noise, not part of the code.
    static string Normalize(string value)
        => new([.. value.Where(Char.IsLetterOrDigit).Select(Char.ToUpperInvariant)]);
}
