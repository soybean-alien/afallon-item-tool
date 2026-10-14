// Afallon Item Tool by soybean_alien
// https://github.com/soybean-alien/afallon-item-tool
// Licensed under the GNU GPL v3 with additional terms (author attribution required,
// GPL-3.0 section 7(b)/(c)). See LICENSE and NOTICE.md.

using AfallonItemSpawner.Core;
using BLINK.RPGBuilder.Managers;

namespace AfallonItemSpawner.BepInEx;

/// <summary>
/// Grants items through the same entry point the game uses for quest rewards and its own
/// developer "Get Item" panel: RPGBuilderUtilities.HandleItemLooting(itemID, itemDataID, amount, false, show).
/// itemDataID -1 makes the game generate fresh item data (random stats, sockets, ...). The call returns
/// how many items could NOT be added (verified live: 0 on success, 1 for one item into full bags,
/// 5 for five potions into full bags).
///
/// Corruption: gear corruption is CharacterEntries.ItemEntry.corruptionLevel (the game derives stat
/// bonuses from it via CorruptionGearBonus); corruption tokens carry their level in corruptionValue.
/// To set either, the item data is generated first with HandleNewItemDATA(itemID, InBag) - exactly
/// what HandleItemLooting does internally for -1 - the level is written to that entry, and the
/// prepared id is passed to HandleItemLooting. Capacity is checked with canGetItem first so no
/// orphan item data is created when the bags are full.
/// </summary>
internal sealed class GameItemGrantService : IItemGrantService
{
    /// <summary>Corruption to apply to the next grant (0 = none). Set by the UI before each request.</summary>
    public int CorruptionLevel { get; set; }


    public SpawnExecutionResult Grant(ItemRecord item, int quantity)
    {
        if (!GameState.IsInGame())
        {
            return SpawnExecutionResult.Failure("Load a character first; items can only be added in-game.");
        }

        // Currency items (Gold, Corrupted emerald) are not bag items: the game converts them to wallet
        // currency, and HandleItemLooting refuses them (verified: returned 999999 of 999999 for Gold).
        // Add the amount to the wallet directly, the way vendors and quest rewards pay currency.
        var currencyId = CurrencyIdFor(item.Id);
        if (currencyId >= 0)
        {
            if (InventoryManager.Instance is null) return SpawnExecutionResult.Failure("Inventory is not ready yet.");
            InventoryManager.Instance.AddCurrency(currencyId, quantity);
            CatalogProbeState.Log?.LogInfo($"AddCurrency({currencyId}, {quantity}) for item {item.Id} ({item.SafeName}).");
            return SpawnExecutionResult.Success($"Added {quantity} {item.SafeName} to your currency.");
        }

        var stackLimit = StackLimitFor(item.Id);
        var token = ItemDetailsIndex.IsCorruptionToken(item.Id);
        var corruption = Math.Clamp(CorruptionLevel, 0, ItemDetailsIndex.MaxCorruptionLevel);
        if (stackLimit > 1 && !(token && corruption > 0))
        {
            // Stackable: one call, like quest rewards. The game caps it to what fits.
            // Verified live: the return value is the number of items that did NOT fit
            // (0 = all added; 5 of 5 requested into full bags returned 5).
            var notAdded = RPGBuilderUtilities.HandleItemLooting(item.Id, -1, quantity, false, true);
            CatalogProbeState.Log?.LogInfo($"HandleItemLooting({item.Id}, -1, {quantity}, false, true) returned {notAdded}.");
            if (notAdded <= 0) return SpawnExecutionResult.Success($"Added {quantity} x {item.SafeName}.");
            var stackAdded = quantity - Math.Min(notAdded, quantity);
            return stackAdded > 0
                ? SpawnExecutionResult.Success($"Inventory filled up: added {stackAdded} of {quantity} x {item.SafeName}.")
                : SpawnExecutionResult.Failure($"Inventory is full; {item.SafeName} was not added.");
        }

        // One call per copy (like the developer panel) so each copy gets its own item data.
        var added = 0;
        string stopReason = null;
        for (var i = 0; i < quantity; i++)
        {
            var dataId = -1;
            if (corruption > 0)
            {
                if (InventoryManager.Instance is null || InventoryManager.Instance.canGetItem(item.Id, 1) < 1)
                {
                    stopReason = "inventory full";
                    break;
                }

                dataId = PrepareItemData(item.Id, corruption, token);
                if (dataId < 0)
                {
                    stopReason = "could not create item data";
                    break;
                }
            }

            var result = RPGBuilderUtilities.HandleItemLooting(item.Id, dataId, 1, false, i == quantity - 1);
            if (result != 0)
            {
                stopReason = $"game returned code {result}";
                break;
            }

            added++;
        }

        var suffix = corruption > 0 ? $" at corruption +{corruption}" : string.Empty;
        CatalogProbeState.Log?.LogInfo($"Granted {added}/{quantity} x item {item.Id} ({item.SafeName}){suffix}" + (stopReason is null ? "." : $"; stopped: {stopReason}."));
        if (added == quantity)
        {
            return SpawnExecutionResult.Success($"Added {added} x {item.SafeName}{suffix}.");
        }

        return added == 0
            ? SpawnExecutionResult.Failure($"Inventory is full; {item.SafeName} was not added.")
            : SpawnExecutionResult.Success($"Inventory filled up: added {added} of {quantity} x {item.SafeName}{suffix}.");
    }

    private static int PrepareItemData(int itemId, int level, bool token)
    {
        var dataId = RPGBuilderUtilities.HandleNewItemDATA(itemId, CharacterEntries.ItemEntryState.InBag);
        if (dataId < 0) return -1;
        var entry = RPGBuilderUtilities.GetItemDataFromDataID(dataId);
        if (entry is null) return dataId;
        if (level > 0)
        {
            // Tokens: the dungeon reward path names its level "corruptionValue"; tokens get both fields
            // (verified: a +12 token shows "Corruption +12"). Gear uses corruptionLevel.
            if (token) entry.corruptionValue = level;
            entry.corruptionLevel = level;
        }

        return dataId;
    }


    /// <summary>The wallet currency a Currency-category item converts into (RPGItem.convertToCurrency), or -1.</summary>
    private static int CurrencyIdFor(int itemId)
    {
        try
        {
            var index = Array.IndexOf(CatalogProbeState.ItemIds, itemId);
            if (index < 0 || CatalogProbeState.ItemCategories?[index] != "Currency") return -1;
            var items = GameDatabase.Instance?.GetItems();
            if (items is null || !items.ContainsKey(itemId)) return -1;
            var def = items[itemId];
            return def is not null && def.convertToCurrency >= 0 ? def.convertToCurrency : -1;
        }
        catch (Exception exception)
        {
            CatalogProbeState.Log?.LogWarning($"Currency lookup for item {itemId} failed: {exception.Message}");
            return -1;
        }
    }

    private static int StackLimitFor(int itemId)
    {
        var ids = CatalogProbeState.ItemIds;
        var limits = CatalogProbeState.ItemStackLimits;
        var index = Array.IndexOf(ids, itemId);
        return index >= 0 && limits is not null ? limits[index] : 1;
    }
}
