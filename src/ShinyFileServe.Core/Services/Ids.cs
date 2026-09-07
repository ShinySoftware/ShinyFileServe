namespace ShinyFileServe.Core.Services;

/// <summary>
/// Document Ids are assigned here rather than left to the store. String Ids are not auto-generated
/// on insert, and a v7 GUID sorts by creation time, so an activity log paged by Id reads in order.
/// </summary>
public static class Ids
{
    public static string New() => Guid.CreateVersion7().ToString("n");
}
