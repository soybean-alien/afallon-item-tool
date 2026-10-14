// Afallon Item Tool by soybean_alien
// https://github.com/soybean-alien/afallon-item-tool
// Licensed under the GNU GPL v3 with additional terms (author attribution required,
// GPL-3.0 section 7(b)/(c)). See LICENSE and NOTICE.md.

using System.Text.RegularExpressions;
using BepInEx.Logging;
using BLINK.RPGBuilder.Managers;

namespace AfallonItemSpawner.BepInEx;

/// <summary>
/// Builds, from the game's own database, the set of ways each item can be obtained:
/// loot tables, merchant stock, crafting recipes, quest items/rewards, and class/race starting gear.
/// Items with no entry are not referenced by any of those tables. They are usually developer/test
/// records or items handed out only by world objects or scripts this index cannot see, so the UI
/// labels them "no listed source" rather than claiming they are unobtainable.
/// Every table is read independently; one failing table only loses that source.
/// </summary>
internal static class ItemSourceIndex
{
    private static readonly Regex DevName = new(
        @"\b(dev|debug|test|testing|placeholder|unused|deprecated|tmp|wip|dummy)\b|^_",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool LooksLikeDevRecord(string displayName, string internalName) =>
        DevName.IsMatch(displayName ?? string.Empty) || DevName.IsMatch(internalName ?? string.Empty);

    public static Dictionary<int, HashSet<string>> Build(GameDatabase db, ManualLogSource log)
    {
        var map = new Dictionary<int, HashSet<string>>();
        void Add(int id, string source)
        {
            if (id < 0) return;
            if (!map.TryGetValue(id, out var set)) map[id] = set = new HashSet<string>();
            set.Add(source);
        }

        var counts = new List<string>();
        void Read(string label, Action body)
        {
            var before = map.Values.Sum(s => s.Count);
            try
            {
                body();
                counts.Add($"{label}+{map.Values.Sum(s => s.Count) - before}");
            }
            catch (Exception exception)
            {
                counts.Add($"{label}=FAILED({exception.GetType().Name})");
            }
        }

        Read("loot", () =>
        {
            foreach (var pair in db.GetLootTables())
            {
                var items = pair.Value?.lootItems;
                if (items is null) continue;
                foreach (var entry in items) if (entry is not null) Add(entry.itemID, "loot");
            }
        });
        Read("world", () =>
        {
            var economy = db.GetEconomySettings();
            if (economy?.WorldLootTables is null) return;
            var tables = db.GetLootTables();
            foreach (var entry in economy.WorldLootTables)
            {
                if (entry is null || !tables.ContainsKey(entry.lootTableID)) continue;
                var items = tables[entry.lootTableID]?.lootItems;
                if (items is null) continue;
                foreach (var item in items) if (item is not null) Add(item.itemID, "world drop");
            }
        });
        Read("vendor", () =>
        {
            foreach (var pair in db.GetMerchantTables())
            {
                var items = pair.Value?.onSaleItems;
                if (items is null) continue;
                foreach (var entry in items) if (entry is not null) Add(entry.itemID, "vendor");
            }
        });
        Read("craft", () =>
        {
            foreach (var pair in db.GetRecipes())
            {
                var ranks = pair.Value?.ranks;
                if (ranks is null) continue;
                foreach (var rank in ranks)
                {
                    var crafted = rank?.allCraftedItems;
                    if (crafted is null) continue;
                    foreach (var entry in crafted) if (entry is not null) Add(entry.craftedItemID, "craft");
                }
            }
        });
        Read("quest", () =>
        {
            foreach (var pair in db.GetQuests())
            {
                var quest = pair.Value;
                if (quest is null) continue;
                if (quest.itemsGiven is not null)
                    foreach (var entry in quest.itemsGiven) if (entry is not null) Add(entry.itemID, "quest");
                if (quest.rewardsGiven is not null)
                    foreach (var entry in quest.rewardsGiven) if (entry is not null) Add(entry.itemID, "quest reward");
                if (quest.rewardsToPick is not null)
                    foreach (var entry in quest.rewardsToPick) if (entry is not null) Add(entry.itemID, "quest reward");
            }
        });
        Read("start", () =>
        {
            foreach (var pair in db.GetClasses())
            {
                var items = pair.Value?.startItems;
                if (items is null) continue;
                foreach (var entry in items) if (entry is not null) Add(entry.itemID, "starting gear");
            }

            foreach (var pair in db.GetRaces())
            {
                var items = pair.Value?.startItems;
                if (items is null) continue;
                foreach (var entry in items) if (entry is not null) Add(entry.itemID, "starting gear");
            }
        });

        log?.LogInfo($"Item source index: {map.Count} distinct items referenced ({string.Join(", ", counts)}).");
        return map;
    }
}
