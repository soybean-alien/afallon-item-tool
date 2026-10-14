// Afallon Item Tool - Copyright (C) 2026 soybean_alien
// https://github.com/soybean-alien/afallon-item-tool
// Licensed under the GNU GPL v3 with additional terms (author attribution required,
// GPL-3.0 section 7(b)/(c)). See LICENSE and NOTICE.md.

using AfallonItemSpawner.Core;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BLINK.RPGBuilder.Managers;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace AfallonItemSpawner.BepInEx;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "soybean_alien.afallon.itemspawner";
    public const string PluginName = "Afallon Item Tool";
    public const string PluginVersion = "0.3.0-alpha";

    internal static ConfigEntry<bool> AllowQuestItems { get; private set; }
    internal static ConfigEntry<int> MaximumQuantity { get; private set; }
    internal static ConfigEntry<KeyCode> ToggleKey { get; private set; }
    internal static ConfigEntry<float> UiScale { get; private set; }
    internal static ConfigEntry<string> FontName { get; private set; }
    internal static ConfigEntry<string> WindowPosition { get; private set; }
    internal static ConfigEntry<bool> UseGameSkin { get; private set; }
    internal static ConfigEntry<int> InventorySlots { get; private set; }
    internal static ConfigEntry<int> InventorySlotsHighest { get; private set; }
    internal static ConfigEntry<bool> InventoryAllowShrink { get; private set; }

    public override void Load()
    {
        AllowQuestItems = Config.Bind("Safety", "AllowQuestItems", true,
            "Allow adding Quest-category or quest-drop-only items. These can break quest progression. Also toggled from the menu.");
        MaximumQuantity = Config.Bind("Safety", "MaximumQuantity", 999,
            "Largest quantity a single Add click may request (1-9999).");
        ToggleKey = Config.Bind("Controls", "ToggleKey", KeyCode.F8, "Key that shows/hides the spawner window.");
        UiScale = Config.Bind("Display", "UiScale", 1f,
            "Menu size multiplier on top of automatic resolution scaling (1080p = 1.0 base). Range 0.5-3.");
        FontName = Config.Bind("Display", "FontName", string.Empty,
            "TextMeshPro font asset name to use. Empty = pick the font the game's own UI uses most. The log lists candidates.");
        InventorySlots = Config.Bind("Inventory", "InventorySlots", 0,
            "Bag slots for characters (0 = game default, 105 in 0.16.3). Applied before characters load. "
            + "WARNING (verified): if the bag later becomes smaller (setting lowered, or the mod removed), the game trims the save "
            + "and DELETES items stored in the extra slots. Empty the extra slots first.");
        InventorySlotsHighest = Config.Bind("Inventory", "HighestApplied", 0,
            "Managed by the mod: the largest bag size applied so far. The mod never applies less than this unless AllowShrink is true.");
        InventoryAllowShrink = Config.Bind("Inventory", "AllowShrink", false,
            "Set true ONLY after emptying the extra slots, to let InventorySlots go below HighestApplied.");
        UseGameSkin = Config.Bind("Display", "UseGameSkin", true,
            "Dress the window frames, header, buttons and inputs with the game's own UI art (runtime references only). false = flat style.");
        WindowPosition = Config.Bind("Display", "WindowPosition", string.Empty,
            "Saved window offset from screen centre in layout units, \"x,y\". Written when you finish dragging; empty = centred.");

        CatalogProbeState.Initialize(Log);
        AddComponent<CatalogProbe>();
        Log.LogInfo($"{PluginName} {PluginVersion} loaded (uGUI menu; Add works only with a character loaded).");
    }
}

public sealed class CatalogProbe : MonoBehaviour
{
    private const float ProbeIntervalSeconds = 1f;
    private const float TimeoutSeconds = 120f;
    private float _elapsed;
    private float _nextProbe;
    private bool _finished;

    public CatalogProbe(IntPtr pointer) : base(pointer)
    {
    }

    private void Update()
    {
        if (_finished)
        {
            return;
        }

        _elapsed += Time.unscaledDeltaTime;
        if (_elapsed < _nextProbe)
        {
            return;
        }

        _nextProbe = _elapsed + ProbeIntervalSeconds;
        if (_elapsed > TimeoutSeconds)
        {
            CatalogProbeState.Log?.LogWarning("Runtime catalog was not ready within 120 seconds; plugin initialization stopped.");
            _finished = true;
            return;
        }

        if (!CatalogProbeState.TryReadCatalog())
        {
            return;
        }

        _finished = true;
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<ItemSpawnerController>();
            var host = new GameObject("soybean_alien_AfallonItemSpawner");
            DontDestroyOnLoad(host);
            host.AddComponent<ItemSpawnerController>();
        }
        catch (Exception exception)
        {
            CatalogProbeState.Log?.LogError($"Item-spawner UI could not be created: {exception}");
        }
    }
}

public static class CatalogProbeState
{
    public static ManualLogSource Log { get; private set; }
    public static ItemCatalog CurrentCatalog { get; private set; }
    public static int[] ItemIds { get; private set; }
    public static string[] ItemNames { get; private set; }
    public static string[] ItemCategories { get; private set; }
    public static string[] ItemTypes { get; private set; }
    public static bool[] ItemProgressionFlags { get; private set; }
    public static string[] ItemRarities { get; private set; }
    public static Color[] ItemRarityColors { get; private set; }
    public static int[] ItemSellPrices { get; private set; }
    public static int[] ItemStackLimits { get; private set; }
    /// <summary>Where the game database says the item can come from (loot, vendor, craft, quest, start); empty = none listed.</summary>
    public static string[] ItemSources { get; private set; }
    /// <summary>Name looks like a developer/test record (e.g. "DEV RING").</summary>
    public static bool[] ItemDevNames { get; private set; }
    /// <summary>Game-computed level requirement (EconomyUtilities.GetItemLevelRequirement); 0 = none/unknown.</summary>
    public static int[] ItemLevels { get; private set; }
    /// <summary>Class ids allowed to use the item; null = all classes (see ItemClassIndex).</summary>
    public static HashSet<int>[] ItemClassSets { get; private set; }
    public static string[] ItemClassText { get; private set; }
    /// <summary>Fixed stat amounts per item (statID → summed amount) from RPGItem.stats.</summary>
    public static Dictionary<int, float>[] ItemStatMaps { get; private set; }
    /// <summary>Stats that appear on at least one item, for the sort dropdown: (statID, display name).</summary>
    public static (int[] Ids, string Name)[] SortableStats { get; private set; } = Array.Empty<(int[], string)>();
    /// <summary>Stat id of "Item power" (the game's own gear score), or -1.</summary>
    public static int ItemPowerStatId { get; private set; } = -1;
    /// <summary>Game-defined ceilings per stat id (RPGStat.maxCheck → maxValue).</summary>
    public static Dictionary<int, float> StatCaps { get; } = new();
    public static HashSet<int> PercentStats { get; } = new();

    public static void Initialize(ManualLogSource log) => Log = log;

    /// <summary>Re-reads one item's level requirement after an override changed it.</summary>
    public static void RefreshItemLevel(int itemId)
    {
        if (ItemIds is null || ItemLevels is null) return;
        var index = Array.IndexOf(ItemIds, itemId);
        if (index < 0) return;
        try { ItemLevels[index] = EconomyUtilities.GetItemLevelRequirement(GameDatabase.Instance.GetItems()[itemId]); }
        catch { /* keep the old value */ }
    }

    /// <summary>Recomputes one item's stat map after its definition changed (stat overrides).</summary>
    public static void RefreshItemStats(int itemId)
    {
        if (ItemIds is null || ItemStatMaps is null) return;
        var index = Array.IndexOf(ItemIds, itemId);
        if (index < 0) return;
        var map = new Dictionary<int, float>();
        var item = GameDatabase.Instance.GetItems()[itemId];
        if (item?.stats is not null)
            foreach (var stat in item.stats)
                if (stat is not null) map[stat.statID] = (map.TryGetValue(stat.statID, out var v) ? v : 0f) + stat.amount;
        ItemStatMaps[index] = map;
    }

    public static bool TryReadCatalog()
    {
        var database = GameDatabase.Instance;
        if (database is null)
        {
            return false;
        }

        var items = database.GetItems();
        if (items is null || items.Count == 0)
        {
            return false;
        }

        try
        {
            var records = new List<ItemRecord>(items.Count);
            foreach (var pair in items)
            {
                var item = pair.Value;
                if (item is null)
                {
                    continue;
                }

                var name = FirstUseful(item.displayName, item.entryDisplayName, item._name, item.entryName);
                var type = FirstUseful(item.itemType, item.ItemType?.entryDisplayName);
                records.Add(new ItemRecord(
                    pair.Key,
                    name,
                    RuntimeCategoryMapper.Map(type),
                    type,
                    item.QuestDropOnly));
            }

            CurrentCatalog = new ItemCatalog(records);
            var ordered = CurrentCatalog.Search(new CatalogQuery());
            ItemIds = ordered.Select(record => record.Id).ToArray();
            ItemNames = ordered.Select(record => record.SafeName).ToArray();
            ItemCategories = ordered.Select(record => record.Category.ToString()).ToArray();
            ItemTypes = ordered.Select(record => record.Subcategory ?? string.Empty).ToArray();
            ItemProgressionFlags = ordered.Select(record => record.IsProgressionSensitive).ToArray();
            var runtimeItems = ordered.Select(record => items[record.Id]).ToArray();
            ItemRarities = runtimeItems.Select(item => FirstUseful(
                item.ItemRarity?.entryDisplayName,
                item.rarity)).ToArray();
            ItemRarityColors = runtimeItems.Select(item => item.ItemRarity?.color ?? new Color(0.28f, 0.31f, 0.34f, 1f)).ToArray();
            ItemSellPrices = runtimeItems.Select(item => item.sellPrice).ToArray();
            ItemStackLimits = runtimeItems.Select(item => item.stackLimit).ToArray();
            ItemLevels = runtimeItems.Select(item =>
            {
                try { return EconomyUtilities.GetItemLevelRequirement(item); }
                catch { return 0; }
            }).ToArray();
            var sources = ItemSourceIndex.Build(database, Log);
            ItemSources = ItemIds.Select(id => sources.TryGetValue(id, out var s) ? string.Join(", ", s.OrderBy(x => x)) : string.Empty).ToArray();
            ItemDevNames = runtimeItems.Select(item => ItemSourceIndex.LooksLikeDevRecord(
                FirstUseful(item.displayName, item.entryDisplayName), FirstUseful(item.entryName, item._name))).ToArray();
            ItemDetailsIndex.Build(database, Log);
            ApplyInventorySlots(database);
            ItemStatMaps = runtimeItems.Select(item =>
            {
                var map = new Dictionary<int, float>();
                try
                {
                    if (item.stats is not null)
                        foreach (var stat in item.stats)
                            if (stat is not null) map[stat.statID] = (map.TryGetValue(stat.statID, out var v) ? v : 0f) + stat.amount;
                }
                catch
                {
                    // An unreadable stat list only loses that item's stat sort values.
                }

                return map;
            }).ToArray();
            try
            {
                var statNames = new Dictionary<int, string>();
                foreach (var pair in database.GetStats())
                {
                    if (pair.Value is null) continue;
                    statNames[pair.Key] = FirstUseful(pair.Value.displayName, pair.Value.entryDisplayName, pair.Value._name);
                    if (pair.Value.maxCheck) StatCaps[pair.Key] = pair.Value.maxValue;
                    if (pair.Value.isPercentStat) PercentStats.Add(pair.Key);
                }
                // Plain loops: some LINQ lambda shapes here emit NullableAttribute, which this project cannot compile.
                var used = new HashSet<int>();
                foreach (var map in ItemStatMaps)
                    foreach (var key in map.Keys)
                        if (statNames.ContainsKey(key)) used.Add(key);
                // Several stat ids share a display name ("Fire Damage" x6); group them so the sort list
                // shows each name once and sorts by the summed amount.
                var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in used)
                {
                    var name = statNames[id];
                    if (!groups.TryGetValue(name, out var ids)) groups[name] = ids = new List<int>();
                    ids.Add(id);
                }

                var list = new List<(int[], string)>();
                foreach (var pair in groups) list.Add((pair.Value.ToArray(), pair.Key));
                list.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
                SortableStats = list.ToArray();
                ItemPowerStatId = statNames.FirstOrDefault(p => p.Value.Equals("Item power", StringComparison.OrdinalIgnoreCase)).Key;
                if (!statNames.ContainsKey(ItemPowerStatId) || !statNames[ItemPowerStatId].Equals("Item power", StringComparison.OrdinalIgnoreCase)) ItemPowerStatId = -1;
                Log?.LogInfo($"Stat caps (game ceilings): {string.Join(", ", StatCaps.Where(c => statNames.ContainsKey(c.Key)).Select(c => $"{statNames[c.Key]}={c.Value}"))}.");
                Log?.LogInfo($"Sortable item stats: {SortableStats.Length} ({string.Join(", ", SortableStats.Select(s => s.Item2))}); item power stat id {ItemPowerStatId}.");
            }
            catch (Exception exception)
            {
                Log?.LogWarning($"Stat names unavailable for sorting: {exception.GetType().Name}.");
            }

            ItemOverrides.LoadAndApplyAll(Log);
            try
            {
                // Diagnostics: consumables the "Use effect x" multiplier can scale (own actions with gains).
                var candidates = new List<string>();
                foreach (var pair in items)
                {
                    var it = pair.Value;
                    if (it?.GameActions is null || it.UseGameActionsTemplate) continue;
                    foreach (var action in it.GameActions)
                        if (ItemOverrides.IsMultipliable(action)) { candidates.Add($"{FirstUseful(it.displayName, it.entryDisplayName)} ({pair.Key}: {action.type} x{action.Amount})"); break; }
                }

                Log?.LogInfo($"Use-effect multiplier candidates: {candidates.Count}" + (candidates.Count > 0 ? ": " + string.Join("; ", candidates.Take(30)) : string.Empty) + ".");
            }
            catch (Exception exception)
            {
                Log?.LogWarning($"Multiplier candidate scan failed: {exception.Message}");
            }
            ItemClassSets = ItemClassIndex.Build(database, ItemIds, Log);
            ItemClassIndex.BuildPreferences(database, ItemIds, Log);
            ItemClassText = ItemClassSets.Select(set => ItemClassIndex.Describe(set)).ToArray();
            var noSource = ItemSources.Count(string.IsNullOrEmpty);
            Log?.LogInfo($"Item sources: {ItemIds.Length - noSource} items have a listed source, {noSource} have none; "
                + $"{ItemDevNames.Count(x => x)} names look like developer records: "
                + string.Join("; ", Enumerable.Range(0, ItemIds.Length).Where(i => ItemDevNames[i]).Take(25).Select(i => $"{ItemNames[i]} ({ItemIds[i]})")) + ".");
            var counts = ordered
                .GroupBy(record => record.Category)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}={group.Count()}");
            var raritySummary = ItemRarities
                .GroupBy(value => string.IsNullOrWhiteSpace(value) ? "(unranked)" : value.Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{group.Key}={group.Count()}");
            var rarityIds = runtimeItems
                .Where(item => item.ItemRarity is not null)
                .GroupBy(item => item.ItemRarity.ID)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}:{group.First().ItemRarity.entryDisplayName}");
            var goldItems = Enumerable.Range(0, ItemIds.Length)
                .Where(i => string.Equals(ItemRarities[i], "Gold", StringComparison.OrdinalIgnoreCase))
                .Select(i => $"{ItemNames[i]} (ID {ItemIds[i]}, {ItemTypes[i]})");

            Log?.LogInfo($"Runtime catalog ready: {CurrentCatalog.Count} items; {string.Join(", ", counts)}.");
            Log?.LogInfo($"Runtime rarity labels: {string.Join(", ", raritySummary)}.");
            Log?.LogInfo($"Runtime rarity entry IDs: {string.Join(", ", rarityIds)}. Gold-rarity items: {string.Join("; ", goldItems)}.");
            return true;
        }
        catch (Exception exception)
        {
            Log?.LogError($"Runtime catalog probe failed closed: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Bag size lives in RPGBuilderEconomySettings.InventorySlots and RPGItemDATA.InventorySlots.
    /// Set both before a character loads when the user configured a value.
    /// </summary>
    private static void ApplyInventorySlots(GameDatabase database)
    {
        var wanted = Plugin.InventorySlots?.Value ?? 0;
        var highest = Plugin.InventorySlotsHighest?.Value ?? 0;
        var allowShrink = Plugin.InventoryAllowShrink?.Value == true;
        if (!allowShrink && highest > 0 && wanted < highest)
        {
            Log?.LogWarning($"Inventory slots: config asks for {(wanted <= 0 ? "the game default" : wanted.ToString())} but {highest} were applied before; "
                + $"keeping {highest} so items in the extra slots are not deleted (set AllowShrink=true after emptying them).");
            wanted = highest;
        }
        try
        {
            var economy = database.GetEconomySettings();
            var before = economy?.InventorySlots ?? -1;
            if (wanted <= 0)
            {
                Log?.LogInfo($"Inventory slots: game default ({before}).");
                return;
            }

            wanted = Math.Clamp(wanted, 1, 2000);
            if (economy is not null) economy.InventorySlots = wanted;
            var dataAssets = 0;
            foreach (var data in UnityEngine.Resources.FindObjectsOfTypeAll<RPGItemDATA>())
            {
                if (data is null) continue;
                data.InventorySlots = wanted;
                dataAssets++;
            }

            if (Plugin.InventorySlotsHighest is not null)
                Plugin.InventorySlotsHighest.Value = allowShrink ? wanted : Math.Max(highest, wanted);
            Log?.LogInfo($"Inventory slots: {before} -> {wanted} (economy settings + {dataAssets} item-data asset(s)).");
        }
        catch (Exception exception)
        {
            Log?.LogWarning($"Could not change inventory slots: {exception.Message}");
        }
    }

    private static string FirstUseful(params string[] candidates) =>
        candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}

internal static class RuntimeCategoryMapper
{
    public static ItemCategory Map(string rawType)
    {
        var value = rawType?.Trim() ?? string.Empty;
        if (Contains(value, "weapon")) return ItemCategory.Weapon;
        if (Contains(value, "armor")) return ItemCategory.Armor;
        if (Contains(value, "ring", "trinket", "equipment")) return ItemCategory.Equipment;
        if (Contains(value, "consumable", "potion", "food")) return ItemCategory.Consumable;
        if (Contains(value, "reagent", "herb")) return ItemCategory.Reagent;
        if (Contains(value, "craft", "material", "ore")) return ItemCategory.CraftingSupply;
        if (Contains(value, "quest", "key")) return ItemCategory.Quest;
        if (Contains(value, "currency", "coin")) return ItemCategory.Currency;
        return string.IsNullOrWhiteSpace(value) ? ItemCategory.Unknown : ItemCategory.Miscellaneous;
    }

    private static bool Contains(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
