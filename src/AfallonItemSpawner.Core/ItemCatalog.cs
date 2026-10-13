namespace AfallonItemSpawner.Core;

public sealed class ItemCatalog
{
    private readonly IReadOnlyList<ItemRecord> _items;
    private readonly IReadOnlyDictionary<int, ItemRecord> _byId;

    public ItemCatalog(IEnumerable<ItemRecord> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var materialized = items.ToArray();
        var duplicateId = materialized.GroupBy(item => item.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new ArgumentException($"Duplicate item ID {duplicateId.Key}.", nameof(items));
        }

        _items = materialized;
        _byId = materialized.ToDictionary(item => item.Id);
    }

    public int Count => _items.Count;

    public bool TryGet(int id, out ItemRecord? item) => _byId.TryGetValue(id, out item);

    public IReadOnlyList<ItemRecord> Search(CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tokens = (query.Text ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return _items
            .Where(item => !query.Category.HasValue || item.Category == query.Category.Value)
            .Where(item => tokens.All(token => Matches(item, token)))
            .OrderBy(item => item.SafeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .ToArray();
    }

    private static bool Matches(ItemRecord item, string token)
    {
        return item.SafeName.Contains(token, StringComparison.OrdinalIgnoreCase)
            || item.Id.ToString().Contains(token, StringComparison.OrdinalIgnoreCase)
            || item.Category.ToString().Contains(token, StringComparison.OrdinalIgnoreCase)
            || (item.Subcategory?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}

