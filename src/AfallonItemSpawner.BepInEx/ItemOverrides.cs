using System.Globalization;
using BepInEx.Logging;
using BLINK.RPGBuilder.Managers;

namespace AfallonItemSpawner.BepInEx;

/// <summary>
/// User stat overrides on item DEFINITIONS (RPGItem.stats), e.g. "make Ring of immortality insane".
/// Applied in memory at startup and whenever the user presses Apply; stored in
/// BepInEx/config/soybean_alien.afallon.itemspawner.overrides.txt as "itemId|statId=value;statId=value".
///
/// Why definitions and not per-copy random stats: the game's ItemTooltip.Show indexes a copy's random
/// stats against the item's own randomStats definitions, so extra per-copy entries throw
/// ArgumentOutOfRangeException (seen live). Definition stats go through the normal item.stats path that
/// every tooltip and stat calculation already handles. Nothing is written to save files, so removing the
/// mod returns every item to its original stats. Overrides apply to every copy of that item.
/// </summary>
internal static class ItemOverrides
{
    private static readonly Dictionary<int, List<(int StatId, float Value)>> Overrides = new();
    private static readonly Dictionary<int, List<(int StatId, float Amount, bool IsPercent)>> Originals = new();
    /// <summary>Consumable effect multipliers (file option "M=n"): GameAction amounts are multiplied.</summary>
    private static readonly Dictionary<int, int> Multipliers = new();
    private static readonly Dictionary<int, List<(GameActionsData.GameAction Action, int Amount, int Amount2)>> OriginalActions = new();
    /// <summary>Items whose level requirement is forced to 1 (file option "L=1").</summary>
    private static readonly HashSet<int> LevelOne = new();
    private static readonly Dictionary<int, (bool UsedTemplate, List<(RequirementsData.Requirement Req, int Amount)> Levels)> OriginalRequirements = new();
    private static ManualLogSource _log;

    private static string FilePath => Path.Combine(global::BepInEx.Paths.ConfigPath, "soybean_alien.afallon.itemspawner.overrides.txt");

    public static void LoadAndApplyAll(ManualLogSource log)
    {
        _log = log;
        Overrides.Clear();
        try
        {
            if (File.Exists(FilePath))
            {
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 2 || !int.TryParse(parts[0], out var itemId)) continue;
                    for (var k = 2; k < parts.Length; k++)
                    {
                        var option = parts[k].Trim();
                        if (option.Equals("L=1", StringComparison.OrdinalIgnoreCase)) LevelOne.Add(itemId);
                        else if (option.StartsWith("M=", StringComparison.OrdinalIgnoreCase) && int.TryParse(option.Substring(2), out var mult) && mult > 1)
                            Multipliers[itemId] = Math.Min(mult, MaxMultiplier);
                    }
                    var stats = new List<(int, float)>();
                    foreach (var pair in parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = pair.Split('=');
                        if (kv.Length == 2 && int.TryParse(kv[0], out var statId)
                            && float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                            stats.Add((statId, value));
                    }

                    if (stats.Count > 0 || LevelOne.Contains(itemId) || Multipliers.ContainsKey(itemId)) Overrides[itemId] = stats;
                }
            }
        }
        catch (Exception exception)
        {
            log?.LogWarning($"Item overrides file unreadable ({exception.Message}); no overrides applied.");
            return;
        }

        foreach (var itemId in Overrides.Keys.ToList()) ApplyToDefinition(itemId);
        log?.LogInfo($"Item stat overrides: {Overrides.Count} item(s) patched from {FilePath}.");
    }

    public static bool Has(int itemId) => Overrides.ContainsKey(itemId);

    public static bool IsLevelOne(int itemId) => LevelOne.Contains(itemId);

    public const int MaxMultiplier = 100;

    public static int MultiplierOf(int itemId) => Multipliers.TryGetValue(itemId, out var m) ? m : 1;

    /// <summary>Sets a consumable's effect multiplier (1 = remove) and saves.</summary>
    public static void SetMultiplier(int itemId, int multiplier)
    {
        multiplier = Math.Clamp(multiplier, 1, MaxMultiplier);
        if (multiplier > 1) Multipliers[itemId] = multiplier; else Multipliers.Remove(itemId);
        if (!Overrides.ContainsKey(itemId)) Overrides[itemId] = new List<(int, float)>();
        if (multiplier <= 1 && Overrides[itemId].Count == 0 && !LevelOne.Contains(itemId)) Overrides.Remove(itemId);
        ApplyToDefinition(itemId);
        Save();
    }

    public static IReadOnlyList<(int StatId, float Value)> Get(int itemId) =>
        Overrides.TryGetValue(itemId, out var stats) ? stats : (IReadOnlyList<(int, float)>)Array.Empty<(int, float)>();

    /// <summary>Replaces the item's override with these stats, patches the definition and saves the file.</summary>
    public static void Set(int itemId, IEnumerable<(int StatId, float Value)> stats, bool levelOne = false)
    {
        Overrides[itemId] = stats.ToList();
        if (levelOne) LevelOne.Add(itemId); else LevelOne.Remove(itemId);
        ApplyToDefinition(itemId);
        Save();
    }

    /// <summary>Removes the override and restores the item's original stats.</summary>
    public static void Clear(int itemId)
    {
        Overrides.Remove(itemId);
        LevelOne.Remove(itemId);
        Multipliers.Remove(itemId);
        ApplyToDefinition(itemId);
        Save();
    }

    private static void ApplyToDefinition(int itemId)
    {
        try
        {
            var items = GameDatabase.Instance.GetItems();
            if (!items.ContainsKey(itemId)) return;
            var item = items[itemId];
            item.stats ??= new Il2CppSystem.Collections.Generic.List<RPGItem.ITEM_STATS>();

            if (!Originals.ContainsKey(itemId))
            {
                var snapshot = new List<(int, float, bool)>();
                foreach (var s in item.stats) if (s is not null) snapshot.Add((s.statID, s.amount, s.isPercent));
                Originals[itemId] = snapshot;
            }

            item.stats.Clear();
            foreach (var (statId, amount, isPercent) in Originals[itemId])
                item.stats.Add(new RPGItem.ITEM_STATS { statID = statId, amount = amount, isPercent = isPercent });
            if (Overrides.TryGetValue(itemId, out var extra))
                foreach (var (statId, value) in extra)
                    item.stats.Add(new RPGItem.ITEM_STATS { statID = statId, amount = value, isPercent = CatalogProbeState.PercentStats.Contains(statId) });

            ApplyLevelRequirement(item, itemId);
            ApplyMultiplier(item, itemId);
            CatalogProbeState.RefreshItemStats(itemId);
            CatalogProbeState.RefreshItemLevel(itemId);
        }
        catch (Exception exception)
        {
            _log?.LogWarning($"Could not apply stat override to item {itemId}: {exception.Message}");
        }
    }

    /// <summary>
    /// The level requirement lives in item.Requirements as RequirementType.Level entries (that is what
    /// EconomyUtilities.GetItemLevelRequirement walks). For "L=1" those entries are set to 1; if the item
    /// used a shared RequirementsTemplate, its groups are copied onto the item first and the template is
    /// switched off for this item only, so no other item changes. Clear restores the originals.
    /// </summary>
    private static void ApplyLevelRequirement(RPGItem item, int itemId)
    {
        if (!OriginalRequirements.ContainsKey(itemId))
        {
            if (!LevelOne.Contains(itemId)) return; // never touched
            item.Requirements ??= new Il2CppSystem.Collections.Generic.List<RequirementsData.RequirementGroup>();
            var usedTemplate = item.UseRequirementsTemplate;
            if (usedTemplate && item.RequirementsTemplate?.Requirements is not null)
                foreach (var group in item.RequirementsTemplate.Requirements)
                    if (group is not null) item.Requirements.Add(group);
            var levels = new List<(RequirementsData.Requirement, int)>();
            foreach (var group in item.Requirements)
            {
                if (group?.Requirements is null) continue;
                foreach (var req in group.Requirements)
                    if (req is not null && req.type == RequirementsData.RequirementType.Level) levels.Add((req, req.Amount1));
            }

            OriginalRequirements[itemId] = (usedTemplate, levels);
        }

        var (wasTemplate, levelReqs) = OriginalRequirements[itemId];
        var before = SafeLevel(item);
        if (LevelOne.Contains(itemId))
        {
            item.UseRequirementsTemplate = false;
            foreach (var (req, _) in levelReqs) req.Amount1 = 1;
        }
        else
        {
            foreach (var (req, amount) in levelReqs) req.Amount1 = amount;
            item.UseRequirementsTemplate = wasTemplate;
        }

        _log?.LogInfo($"Item {itemId} level requirement: {before} -> {SafeLevel(item)} ({levelReqs.Count} level rule(s){(wasTemplate ? ", template detached" : string.Empty)}).");
    }

    /// <summary>
    /// Multiplies the Amount fields of the item's own GameActions. Items that use a shared
    /// GameActionsTemplate are left alone (changing the template would change other items).
    /// </summary>
    private static void ApplyMultiplier(RPGItem item, int itemId)
    {
        if (!OriginalActions.ContainsKey(itemId))
        {
            if (!Multipliers.ContainsKey(itemId)) return;
            if (item.UseGameActionsTemplate || item.GameActions is null)
            {
                _log?.LogWarning($"Item {itemId}: effect multiplier skipped (uses a shared actions template or has no actions).");
                return;
            }

            var snapshot = new List<(GameActionsData.GameAction, int, int)>();
            foreach (var action in item.GameActions) if (action is not null) snapshot.Add((action, action.Amount, action.Amount2));
            OriginalActions[itemId] = snapshot;
        }

        var factor = MultiplierOf(itemId);
        foreach (var (action, amount, amount2) in OriginalActions[itemId])
        {
            if (!IsMultipliable(action)) continue;
            action.Amount =(int)Math.Min(int.MaxValue, (long)amount * factor);
            action.Amount2 = (int)Math.Min(int.MaxValue, (long)amount2 * factor);
        }

        _log?.LogInfo($"Item {itemId}: use effect x{factor} ({OriginalActions[itemId].Count} action(s)).");
    }

    /// <summary>Only gains are scaled: Item actions (often "consume itself") and effects are never multiplied.</summary>
    public static bool IsMultipliable(GameActionsData.GameAction action) =>
        action is not null && action.Amount != 0 && action.type switch
        {
            GameActionsData.GameActionType.Currency or GameActionsData.GameActionType.Point or GameActionsData.GameActionType.Faction
                or GameActionsData.GameActionType.Skill or GameActionsData.GameActionType.WeaponTemplate
                or GameActionsData.GameActionType.Resource => true,
            _ => false
        };

    private static int SafeLevel(RPGItem item)
    {
        try { return EconomyUtilities.GetItemLevelRequirement(item); }
        catch { return -1; }
    }

    private static void Save()
    {
        try
        {
            var lines = new List<string>
            {
                "# Afallon Item Tool - item stat overrides (personal use).",
                "# Format: itemId|statId=value;statId=value[|L=1][|M=n]   L=1 = usable at level 1, M=n = consumable effect x n.",
                "# Delete a line (or use Clear in the menu) to restore an item.",
            };
            foreach (var pair in Overrides.OrderBy(p => p.Key))
                lines.Add($"{pair.Key}|" + string.Join(";", pair.Value.Select(s => $"{s.StatId}={s.Value.ToString(CultureInfo.InvariantCulture)}"))
                    + (LevelOne.Contains(pair.Key) ? "|L=1" : string.Empty)
                    + (Multipliers.TryGetValue(pair.Key, out var m) ? $"|M={m}" : string.Empty));
            File.WriteAllLines(FilePath, lines);
        }
        catch (Exception exception)
        {
            _log?.LogWarning($"Could not save item overrides: {exception.Message}");
        }
    }
}
