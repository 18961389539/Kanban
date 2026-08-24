namespace Kanban.Contracts.Dtos;

/// <summary>Collector 向 Remote 展示端下发的一条本地化覆盖值。</summary>
public sealed record LocalizationOverrideDto
{
    public required string Resource { get; init; }
    public required string Key { get; init; }
    public required string CultureName { get; init; }
    public required string Value { get; init; }
}

/// <summary>Collector 保存语言或覆盖文件后推送给已连接展示端的本地化快照。</summary>
public sealed record LocalizationChangedDto
{
    public required string LanguageCode { get; init; }
    public List<LocalizationOverrideDto> Overrides { get; init; } = [];
}