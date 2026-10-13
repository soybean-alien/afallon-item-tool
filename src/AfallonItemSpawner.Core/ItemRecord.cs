namespace AfallonItemSpawner.Core;

public sealed record ItemRecord(
    int Id,
    string Name,
    ItemCategory Category,
    string? Subcategory = null,
    bool IsProgressionSensitive = false)
{
    public string SafeName => string.IsNullOrWhiteSpace(Name) ? $"Item {Id}" : Name.Trim();
}

