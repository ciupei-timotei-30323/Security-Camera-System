namespace Security_Camera_Backend.Models;

public class User
{
    public int Id { get; set; }

    public string SystemId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
