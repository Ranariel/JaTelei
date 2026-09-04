namespace JaTelei.Client;

public static class AppVersion
{
    /// <summary>
    /// Versão completa do build (ex: 1.0.87). Substituída pelo CI.
    /// Usada internamente para comparação com o servidor de atualização.
    /// </summary>
    public const string Current = "1.0.0";

    /// <summary>
    /// Versão amigável exibida na UI: apenas major.minor (ex: "1.0").
    /// </summary>
    public static string Display
    {
        get
        {
            var parts = Current.Split('.');
            return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : Current;
        }
    }

    /// <summary>Nome completo exibido na UI e nos instaladores.</summary>
    public static string DisplayName => $"Já Telei Beta {Display}";
}
