// Afallon Item Tool - Copyright (C) 2026 soybean_alien
// https://github.com/soybean-alien/afallon-item-tool
// Licensed under the GNU GPL v3 with additional terms (author attribution required,
// GPL-3.0 section 7(b)/(c)). See LICENSE and NOTICE.md.

using BepInEx.Logging;
using BLINK.RPGBuilder.Managers;

namespace AfallonItemSpawner.BepInEx;

/// <summary>
/// Which player classes can use each item, from the game database:
/// 1) explicit Class requirements on the item (or its RequirementsTemplate): Comparison Equal = only
///    those classes, Different = everyone except them;
/// 2) for weapons, RPGClass.AllowedWeaponTypes (a class may wield the weapon's type);
/// 3) otherwise no restriction ("all classes").
/// The result is a set of class ids per item, or null for "all classes".
/// </summary>
internal static class ItemClassIndex
{
    public static (int Id, string Name)[] Classes { get; private set; } = Array.Empty<(int, string)>();

    /// <summary>
    /// Per class: stat id → weight (0..1). Taken from RPGClass.stats: stats the class gains per level
    /// (bonusPerLevel) define what the class scales with; if a class has no per-level growth, its base
    /// amounts are used. Normalised so the class's top stat weighs 1.
    /// </summary>
    public static Dictionary<int, Dictionary<int, float>> ClassStatWeights { get; } = new();

    public static HashSet<int>[] Build(GameDatabase db, IReadOnlyList<int> itemIds, ManualLogSource log)
    {
        var result = new HashSet<int>[itemIds.Count];
        var allowedWeaponTypes = new Dictionary<int, HashSet<int>>();
        var classes = new List<(int, string)>();
        try
        {
            foreach (var pair in db.GetClasses())
            {
                var cls = pair.Value;
                if (cls is null) continue;
                var name = First(cls.displayName, cls.entryDisplayName, cls._name);
                classes.Add((pair.Key, name));
                var growth = new Dictionary<int, float>();
                var baseAmounts = new Dictionary<int, float>();
                if (cls.stats is not null)
                {
                    foreach (var s in cls.stats)
                    {
                        if (s is null || s.statID < 0) continue;
                        if (s.bonusPerLevel > 0f) growth[s.statID] = s.bonusPerLevel;
                        if (s.amount > 0f && !s.isPercent) baseAmounts[s.statID] = s.amount;
                    }
                }

                var source = growth.Count > 0 ? growth : baseAmounts;
                var top = source.Count > 0 ? source.Values.Max() : 0f;
                ClassStatWeights[pair.Key] = top > 0f
                    ? source.ToDictionary(p => p.Key, p => p.Value / top)
                    : new Dictionary<int, float>();

                var set = new HashSet<int>();
                if (cls.AllowedWeaponTypes is not null)
                    foreach (var wt in cls.AllowedWeaponTypes)
                        if (wt is not null) set.Add(wt.ID);
                allowedWeaponTypes[pair.Key] = set;
            }
        }
        catch (Exception exception)
        {
            log?.LogWarning($"Class index: reading classes failed ({exception.GetType().Name}); class info disabled.");
            return result;
        }

        Classes = classes.OrderBy(c => c.Item2, StringComparer.OrdinalIgnoreCase).ToArray();
        var allIds = new HashSet<int>(classes.Select(c => c.Item1));
        var items = db.GetItems();
        int explicitCount = 0, weaponCount = 0, failures = 0;

        for (var i = 0; i < itemIds.Count; i++)
        {
            try
            {
                var item = items[itemIds[i]];
                if (item is null) continue;
                HashSet<int> allowed = null;

                // 1) Explicit class requirements.
                var include = new HashSet<int>();
                var exclude = new HashSet<int>();
                CollectClassRules(item.Requirements, include, exclude);
                if (item.UseRequirementsTemplate && item.RequirementsTemplate is not null)
                    CollectClassRules(item.RequirementsTemplate.Requirements, include, exclude);
                if (include.Count > 0 || exclude.Count > 0)
                {
                    allowed = include.Count > 0 ? new HashSet<int>(include) : new HashSet<int>(allIds);
                    allowed.ExceptWith(exclude);
                    explicitCount++;
                }

                // 2) Weapon type proficiency.
                var weaponTypeId = item.WeaponType?.ID ?? -1;
                if (weaponTypeId >= 0 && item.maxDamage > 0)
                {
                    var wielders = new HashSet<int>(allowedWeaponTypes.Where(p => p.Value.Contains(weaponTypeId)).Select(p => p.Key));
                    if (wielders.Count > 0 && wielders.Count < allIds.Count)
                    {
                        if (allowed is null) allowed = wielders; else allowed.IntersectWith(wielders);
                        weaponCount++;
                    }
                }

                result[i] = allowed;
            }
            catch
            {
                failures++;
            }
        }

        log?.LogInfo($"Class index: {Classes.Length} classes ({string.Join(", ", Classes.Select(c => c.Item2))}); "
            + $"{explicitCount} items with class requirements, {weaponCount} weapons restricted by class weapon types"
            + (failures > 0 ? $", {failures} items unreadable" : string.Empty) + ".");
        return result;
    }

    // ---------- class loot preference (the game's own companion/adventurer logic) ----------

    private static readonly Dictionary<int, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>> ClassPreferences = new();
    private static readonly Dictionary<int, string> ClassPreferenceLabels = new();
    private static readonly Dictionary<int, float[]> ScoreCache = new();
    private static IReadOnlyList<int> _itemIds;

    /// <summary>
    /// For each class, ask the game which stat set its AI adventurers value:
    /// CompanionLootManager.GetPreferenceStats(npc) for every RPGNpc whose AdventurerClassID is that
    /// class (majority vote). The set is one of the game's static arrays Caster/Melee/Tank/Agility/
    /// ShieldPreferenceStats. Items are then scored with the game's own
    /// CompanionLootManager.GetItemPreferenceValue(item, set, -1).
    /// </summary>
    public static void BuildPreferences(GameDatabase db, IReadOnlyList<int> itemIds, ManualLogSource log)
    {
        _itemIds = itemIds;
        var named = new List<(string Name, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> Set)>();
        void AddSet(string name, Func<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>> get)
        {
            try { var s = get(); if (s is not null) named.Add((name, s)); } catch { /* set unavailable */ }
        }

        AddSet("Caster", () => BLINK.RPGBuilder.World.CompanionLootManager.CasterPreferenceStats);
        AddSet("Melee", () => BLINK.RPGBuilder.World.CompanionLootManager.MeleePreferenceStats);
        AddSet("Tank", () => BLINK.RPGBuilder.World.CompanionLootManager.TankPreferenceStats);
        AddSet("Agility", () => BLINK.RPGBuilder.World.CompanionLootManager.AgilityPreferenceStats);
        AddSet("Shield", () => BLINK.RPGBuilder.World.CompanionLootManager.ShieldPreferenceStats);

        var statNames = new Dictionary<int, string>();
        try
        {
            foreach (var pair in db.GetStats())
                if (pair.Value is not null) statNames[pair.Key] = First(pair.Value.displayName, pair.Value.entryDisplayName, pair.Value._name);
        }
        catch { /* names are cosmetic */ }

        string SetName(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> set)
        {
            foreach (var (name, candidate) in named)
            {
                if (candidate.Length != set.Length) continue;
                var same = true;
                for (var k = 0; k < set.Length && same; k++) same = candidate[k] == set[k];
                if (same) return name;
            }

            return "Custom";
        }

        string Stats(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> set)
        {
            var parts = new List<string>();
            for (var k = 0; k < set.Length; k++) parts.Add(statNames.TryGetValue(set[k], out var n) ? n : set[k].ToString());
            return string.Join(", ", parts);
        }

        foreach (var (name, set) in named) log?.LogInfo($"Game preference set {name}: {Stats(set)}.");

        var votes = new Dictionary<int, Dictionary<string, int>>();
        var picks = new Dictionary<int, Dictionary<string, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>>>();
        try
        {
            foreach (var pair in db.GetNPCs())
            {
                var npc = pair.Value;
                if (npc is null || npc.AdventurerClassID < 0 || !Classes.Any(c => c.Id == npc.AdventurerClassID)) continue;
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> set;
                try { set = BLINK.RPGBuilder.World.CompanionLootManager.GetPreferenceStats(npc); }
                catch { continue; }
                if (set is null || set.Length == 0) continue;
                var label = SetName(set);
                if (!votes.TryGetValue(npc.AdventurerClassID, out var v)) { votes[npc.AdventurerClassID] = v = new Dictionary<string, int>(); picks[npc.AdventurerClassID] = new(); }
                v[label] = v.TryGetValue(label, out var count) ? count + 1 : 1;
                picks[npc.AdventurerClassID][label] = set;
            }
        }
        catch (Exception exception)
        {
            log?.LogWarning($"Class preferences: NPC scan failed ({exception.GetType().Name}).");
        }

        foreach (var (classId, className) in Classes)
        {
            if (votes.TryGetValue(classId, out var v) && v.Count > 0)
            {
                var winner = v.OrderByDescending(p => p.Value).First().Key;
                ClassPreferences[classId] = picks[classId][winner];
                ClassPreferenceLabels[classId] = $"{winner} stats ({Stats(picks[classId][winner])})";
                log?.LogInfo($"Class {className}: {ClassPreferenceLabels[classId]} - votes {string.Join(", ", v.Select(p => $"{p.Key}={p.Value}"))}.");
            }
            else
            {
                // GetPreferenceStats falls through to the Melee set when nothing more specific applies,
                // so a class without AI adventurers gets the game's default (Melee) set too.
                var melee = named.FirstOrDefault(n => n.Name == "Melee").Set;
                if (melee is not null)
                {
                    ClassPreferences[classId] = melee;
                    ClassPreferenceLabels[classId] = $"Melee stats ({Stats(melee)})";
                    log?.LogInfo($"Class {className}: no AI adventurer of this class; using the game's default Melee set ({Stats(melee)}).");
                }
                else
                {
                    log?.LogInfo($"Class {className}: no AI adventurer of this class and no Melee set; falling back to class stat weights.");
                }
            }
        }
    }

    public static string PreferenceLabel(int classId) =>
        ClassPreferenceLabels.TryGetValue(classId, out var label) ? label : "class stats";

    /// <summary>
    /// How good item #index is for the class: the game's GetItemPreferenceValue when the class has a
    /// preference set, otherwise the class-stat-weight fallback. Cached per class.
    /// </summary>
    public static float ScoreFor(int classId, int index, Dictionary<int, float> itemStats)
    {
        if (!ScoreCache.TryGetValue(classId, out var scores))
        {
            scores = new float[_itemIds?.Count ?? 0];
            if (ClassPreferences.TryGetValue(classId, out var set) && _itemIds is not null)
            {
                var items = GameDatabase.Instance.GetItems();
                for (var i = 0; i < _itemIds.Count; i++)
                {
                    try { scores[i] = BLINK.RPGBuilder.World.CompanionLootManager.GetItemPreferenceValue(items[_itemIds[i]], set, -1); }
                    catch { scores[i] = 0f; }
                }
            }
            else
            {
                for (var i = 0; i < scores.Length; i++) scores[i] = float.NaN; // use fallback below
            }

            ScoreCache[classId] = scores;
        }

        if (index >= 0 && index < scores.Length && !float.IsNaN(scores[index])) return scores[index];
        return FallbackScore(classId, itemStats);
    }

    private static float FallbackScore(int classId, Dictionary<int, float> itemStats)
    {
        if (itemStats is null || !ClassStatWeights.TryGetValue(classId, out var weights) || weights.Count == 0) return 0f;
        var score = 0f;
        foreach (var pair in itemStats)
            if (weights.TryGetValue(pair.Key, out var weight)) score += pair.Value * weight;
        return score;
    }

    public static string Describe(HashSet<int> allowed)
    {
        if (allowed is null) return "All classes";
        if (allowed.Count == 0) return "No class";
        return string.Join(", ", Classes.Where(c => allowed.Contains(c.Id)).Select(c => c.Name));
    }

    private static void CollectClassRules(Il2CppSystem.Collections.Generic.List<RequirementsData.RequirementGroup> groups, HashSet<int> include, HashSet<int> exclude)
    {
        if (groups is null) return;
        foreach (var group in groups)
        {
            if (group?.Requirements is null) continue;
            foreach (var requirement in group.Requirements)
            {
                if (requirement is null || requirement.type != RequirementsData.RequirementType.Class || requirement.ClassID < 0) continue;
                if (requirement.Comparison == RequirementsData.Comparison.Different) exclude.Add(requirement.ClassID);
                else include.Add(requirement.ClassID);
            }
        }
    }

    private static string First(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
}
