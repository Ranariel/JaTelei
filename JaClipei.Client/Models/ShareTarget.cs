namespace JaClipei.Client.Models;

public enum ShareType { Screen, Window, Game }

public class ShareTarget
{
    public ShareType Type         { get; init; }
    public IntPtr    WindowHandle { get; init; }
    public string    DisplayName  { get; init; } = "";
    /// <summary>Para Type=Screen com múltiplos monitores; null = monitor principal.</summary>
    public System.Windows.Rect? MonitorBounds { get; init; }
}
