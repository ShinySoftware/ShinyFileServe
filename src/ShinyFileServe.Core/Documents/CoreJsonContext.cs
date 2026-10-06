using System.Text.Json.Serialization;

namespace ShinyFileServe.Core.Documents;

/// <summary>
/// Metadata for everything DocumentDb persists. The store is configured with
/// <c>UseReflectionFallback = false</c>, so a type missing here throws rather than silently
/// falling back to reflection and breaking a trimmed desktop build.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Role))]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(ShareFolder))]
[JsonSerializable(typeof(FolderGrant))]
[JsonSerializable(typeof(QuotaUsage))]
[JsonSerializable(typeof(ActivityLogEntry))]
[JsonSerializable(typeof(BandwidthUsage))]
[JsonSerializable(typeof(ServerSettings))]
[JsonSerializable(typeof(PathAnnotation))]
[JsonSerializable(typeof(PasswordRules))]
[JsonSerializable(typeof(AccessToken))]
[JsonSerializable(typeof(PackageFeedSettings))]
[JsonSerializable(typeof(PackageRequestLogEntry))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<ShinyFileServe.Shared.ActivityAction>))]
public partial class CoreJsonContext : JsonSerializerContext;
