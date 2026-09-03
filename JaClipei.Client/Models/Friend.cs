namespace JaClipei.Client.Models;

public class Friend
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTime? LastSeen { get; set; }
    public bool IsOnline => LastSeen.HasValue && (DateTime.UtcNow - LastSeen.Value).TotalMinutes < 5;
}
