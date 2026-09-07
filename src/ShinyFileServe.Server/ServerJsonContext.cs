using System.Text.Json.Serialization;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server;

/// <summary>
/// Every type that crosses an endpoint boundary. The HTTP server never reflects over a type, so a
/// missing entry here is build warning SWS006 rather than a runtime surprise on a trimmed head.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(CurrentUser))]
[JsonSerializable(typeof(QuotaState))]
[JsonSerializable(typeof(FolderInfo))]
[JsonSerializable(typeof(IReadOnlyList<FolderInfo>))]
[JsonSerializable(typeof(List<FolderInfo>))]
[JsonSerializable(typeof(FileEntryInfo))]
[JsonSerializable(typeof(IReadOnlyList<FileEntryInfo>))]
[JsonSerializable(typeof(List<FileEntryInfo>))]
[JsonSerializable(typeof(DirectoryListing))]
[JsonSerializable(typeof(CreateDirectoryRequest))]
[JsonSerializable(typeof(DeleteRequest))]
[JsonSerializable(typeof(OperationResult))]
[JsonSerializable(typeof(ActivityEntryInfo))]
[JsonSerializable(typeof(IReadOnlyList<ActivityEntryInfo>))]
[JsonSerializable(typeof(List<ActivityEntryInfo>))]
[JsonSerializable(typeof(PagedResponse<ActivityEntryInfo>))]
[JsonSerializable(typeof(ServerStatus))]
[JsonSerializable(typeof(ServerConfig))]
[JsonSerializable(typeof(UpdateServerConfig))]
[JsonSerializable(typeof(SessionPolicy))]
[JsonSerializable(typeof(PasswordPolicyInfo))]
[JsonSerializable(typeof(SetupRequest))]
[JsonSerializable(typeof(UserSummary))]
[JsonSerializable(typeof(IReadOnlyList<UserSummary>))]
[JsonSerializable(typeof(List<UserSummary>))]
[JsonSerializable(typeof(UserDetail))]
[JsonSerializable(typeof(IReadOnlyList<UserDetail>))]
[JsonSerializable(typeof(List<UserDetail>))]
[JsonSerializable(typeof(SaveUserRequest))]
[JsonSerializable(typeof(RoleInfo))]
[JsonSerializable(typeof(IReadOnlyList<RoleInfo>))]
[JsonSerializable(typeof(List<RoleInfo>))]
[JsonSerializable(typeof(SaveRoleRequest))]
[JsonSerializable(typeof(GrantInfo))]
[JsonSerializable(typeof(IReadOnlyList<GrantInfo>))]
[JsonSerializable(typeof(List<GrantInfo>))]
[JsonSerializable(typeof(ShareInfo))]
[JsonSerializable(typeof(IReadOnlyList<ShareInfo>))]
[JsonSerializable(typeof(List<ShareInfo>))]
[JsonSerializable(typeof(ShareDetail))]
[JsonSerializable(typeof(SaveShareRequest))]
[JsonSerializable(typeof(SetAnnotationRequest))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<ActivityAction>))]
[JsonSerializable(typeof(List<ActivityAction>))]
public partial class ServerJsonContext : JsonSerializerContext;
