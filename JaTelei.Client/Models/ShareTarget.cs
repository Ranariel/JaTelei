namespace JaTelei.Client.Models;

public enum ShareType { Screen, Window, Game }

public class ShareTarget
{
    public ShareType Type         { get; init; }
    public IntPtr    WindowHandle { get; init; }
    public string    DisplayName  { get; init; } = "";
    /// <summary>Para Type=Screen com múltiplos monitores; null = monitor principal.</summary>
    public System.Windows.Rect? MonitorBounds { get; init; }

    /// <summary>
    /// Altura de destino do frame em pixels (0 = nativa, sem redimensionamento).
    /// Ex: 720 -> resolve para 1280x720 mantendo proporcao.
    /// </summary>
    public int ResolutionHeight { get; init; } = 720;

    /// <summary>Quadros por segundo (30, 60 ou 120).</summary>
    public int Fps { get; init; } = 30;
}
