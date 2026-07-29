namespace LumoSys.Integraciones.Domain.Shared.Interfaces;

public sealed class SolicitudReadData
{
    public string KeyCode { get; init; } = string.Empty;
    public string KeyProcess { get; init; } = "REPORT";
    public int Page { get; init; } = 1;
    public int ItemForPage { get; init; } = 100;
    public string? InfoSort { get; init; }
    public List<CondicionSICAS> Conditions { get; init; } = [];
}

public sealed class CondicionSICAS
{
    public string Label { get; init; } = string.Empty;
    public int FilterType { get; init; }
    public int SubFilter { get; init; }
    public string Values { get; init; } = string.Empty;
    public string Texts { get; init; } = string.Empty;
    public int PosTitle { get; init; }
    public int ChangeTable { get; init; } = -1;
    public string ColumnName { get; init; } = string.Empty;
}
