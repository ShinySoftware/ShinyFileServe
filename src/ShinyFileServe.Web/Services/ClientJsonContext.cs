using System.Text.Json.Serialization;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Web.Services;

/// <summary>
/// The WASM runtime is trimmed hard, so every payload shape is declared here and read through
/// source-generated metadata rather than reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(CurrentUser))]
[JsonSerializable(typeof(FolderInfo))]
[JsonSerializable(typeof(List<FolderInfo>))]
[JsonSerializable(typeof(FileEntryInfo))]
[JsonSerializable(typeof(List<FileEntryInfo>))]
[JsonSerializable(typeof(DirectoryListing))]
[JsonSerializable(typeof(CreateDirectoryRequest))]
[JsonSerializable(typeof(PagedResponse<ActivityEntryInfo>))]
[JsonSerializable(typeof(ActivityEntryInfo))]
[JsonSerializable(typeof(ServerStatus))]
[JsonSerializable(typeof(ServerConfig))]
[JsonSerializable(typeof(UpdateServerConfig))]
[JsonSerializable(typeof(SessionPolicy))]
[JsonSerializable(typeof(SetAnnotationRequest))]
[JsonSerializable(typeof(SetupRequest))]
[JsonSerializable(typeof(UserSummary))]
[JsonSerializable(typeof(List<UserSummary>))]
[JsonSerializable(typeof(ActivityQuery))]
[JsonSerializable(typeof(UserDetail))]
[JsonSerializable(typeof(List<UserDetail>))]
[JsonSerializable(typeof(SaveUserRequest))]
[JsonSerializable(typeof(RoleInfo))]
[JsonSerializable(typeof(List<RoleInfo>))]
[JsonSerializable(typeof(SaveRoleRequest))]
[JsonSerializable(typeof(GrantInfo))]
[JsonSerializable(typeof(List<GrantInfo>))]
[JsonSerializable(typeof(ShareInfo))]
[JsonSerializable(typeof(List<ShareInfo>))]
[JsonSerializable(typeof(ShareDetail))]
[JsonSerializable(typeof(SaveShareRequest))]
public partial class ClientJsonContext : JsonSerializerContext;
