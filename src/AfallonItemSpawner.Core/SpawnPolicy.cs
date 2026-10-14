// Afallon Item Tool by soybean_alien
// https://github.com/soybean-alien/afallon-item-tool
// Licensed under the GNU GPL v3 with additional terms (author attribution required,
// GPL-3.0 section 7(b)/(c)). See LICENSE and NOTICE.md.

namespace AfallonItemSpawner.Core;

public sealed class SpawnPolicy
{
    public SpawnPolicy(int maximumQuantity = 999, bool allowProgressionSensitiveItems = false)
    {
        if (maximumQuantity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumQuantity));
        }

        MaximumQuantity = maximumQuantity;
        AllowProgressionSensitiveItems = allowProgressionSensitiveItems;
    }

    public int MaximumQuantity { get; }
    public bool AllowProgressionSensitiveItems { get; }

    public SpawnValidationResult Validate(ItemCatalog catalog, SpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Quantity < 1 || request.Quantity > MaximumQuantity)
        {
            return SpawnValidationResult.Deny(
                $"Quantity must be between 1 and {MaximumQuantity}.");
        }

        if (!catalog.TryGet(request.ItemId, out var item) || item is null)
        {
            return SpawnValidationResult.Deny("Item is not present in the current runtime catalog.");
        }

        if (!AllowProgressionSensitiveItems
            && (item.IsProgressionSensitive || item.Category == ItemCategory.Quest))
        {
            return SpawnValidationResult.Deny(
                "Quest or progression-sensitive items are blocked by the current safety policy.");
        }

        return SpawnValidationResult.Allow(item);
    }
}

