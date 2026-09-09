namespace ShinyFileServe.Web.Services;

/// <summary>
/// Turns a day off a date picker into the instants a report is actually asked for.
/// </summary>
/// <remarks>
/// <c>&lt;input type="date"&gt;</c> hands back a bare calendar day with no zone on it, and every
/// window on the wire is a <see cref="DateTimeOffset"/>. Stamping the one as the other -
/// <c>new DateTimeOffset(day, TimeSpan.Zero)</c> - is wrong twice over. It throws outright anywhere
/// but Greenwich, because a <see cref="DateTimeKind.Local"/> value may only carry its own offset and
/// <c>DateTime.Today</c> is Local; and where it does not throw it asks for a window sitting hours
/// off the day the operator picked, so an evening's traffic lands in tomorrow and the screen reports
/// that nothing happened.
/// </remarks>
public static class LocalDay
{
    /// <summary>The first instant of a local calendar day.</summary>
    public static DateTimeOffset Start(DateTime day)
        => new DateTimeOffset(DateTime.SpecifyKind(day.Date, DateTimeKind.Local)).ToUniversalTime();

    /// <summary>
    /// The last instant of a local calendar day. A "to" date means the end of that day, or picking
    /// today for both ends of a window returns nothing and reads as a broken filter.
    /// </summary>
    public static DateTimeOffset End(DateTime day)
        => Start(day.Date.AddDays(1)).AddTicks(-1);
}
