namespace Security_Camera_Backend.Services.Auth;

/// <summary>Short claim names embedded in dashboard JWTs.</summary>
public static class AuthClaimTypes
{
    public const string SystemId = "system_id";
    public const string ExternalAdmin = "external_admin";
    public const string Name = "name";
    public const string Role = "role";
}
