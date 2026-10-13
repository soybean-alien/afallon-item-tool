using System.Globalization;
using System.Text;
using BepInEx.Logging;
using BLINK.RPGBuilder.Managers;

namespace AfallonItemSpawner.BepInEx;

/// <summary>
/// Read-only relations from the game database used by the preview panel: who drops an item (NPC,
/// rank, boss dungeon), which gathering node yields it, who sells it and for how much, which recipe
/// crafts it, which quests give/reward/require it, and which classes/races start with it.
/// Built once after the catalog is ready. Every table is read in its own try/catch, so a failure only
/// removes that kind of source from the panel. NPC spawn locations live in scenes, not the database,
/// so ordinary mobs are listed with their level range rather than a zone; bosses are matched to
/// dungeons through each scene's adventure-guide boss list.
/// </summary>
internal static class ItemDetailsIndex
{
    // Plain structs: records emit NullableAttribute metadata this IL2CPP-targeted project cannot compile.
    private readonly struct Drop
    {
        public readonly int LootTableId; public readonly float ItemRate; public readonly int Min; public readonly int Max;
        public Drop(int lootTableId, float itemRate, int min, int max) { LootTableId = lootTableId; ItemRate = itemRate; Min = min; Max = max; }
    }

    private readonly struct Holder
    {
        public readonly string Text; public readonly int Rank;
        public Holder(string text, int rank) { Text = text; Rank = rank; }
    }

    private static readonly Dictionary<int, List<Drop>> DropsByItem = new();
    private static readonly Dictionary<int, List<Holder>> HoldersByLootTable = new();
    private static readonly Dictionary<int, string> LootTableNames = new();
    private static readonly Dictionary<int, List<string>> VendorsByItem = new();
    private static readonly Dictionary<int, List<string>> RecipesByItem = new();
    private static readonly Dictionary<int, List<string>> QuestsByItem = new();
    private static readonly Dictionary<int, List<string>> StartsByItem = new();
    private static readonly Dictionary<int, string> StatNames = new();
    private static readonly Dictionary<int, bool> StatPercent = new();
    private static readonly Dictionary<int, string> CurrencyNames = new();
    private static readonly HashSet<int> CorruptionTokens = new();
    private static readonly Dictionary<string, Dictionary<int, string>> ActionTargets = new();
    private static readonly Dictionary<int, RPGEffect> EffectsById = new();
    private static readonly Dictionary<int, string> NpcNames = new();
    /// <summary>Holder rank for world-loot lines: listed before boss drops, without the "Dropped by" prefix.</summary>
    private const int WorldLootRank = 5;
    private static int _worldLootTables;
    public static int MaxCorruptionLevel { get; private set; } = 30;
    public static float CorruptionAllStatsPercentPerLevel { get; private set; }
    public static bool Ready { get; private set; }

    public static void Build(GameDatabase db, ManualLogSource log)
    {
        var report = new List<string>();
        void Step(string name, Action body)
        {
            try
            {
                body();
                report.Add(name + ":ok");
            }
            catch (Exception exception)
            {
                report.Add($"{name}:FAILED({exception.GetType().Name})");
            }
        }

        var npcNames = new Dictionary<int, string>();
        var bossScenes = new Dictionary<int, List<string>>();

        Step("stats", () =>
        {
            foreach (var pair in db.GetStats())
            {
                if (pair.Value is null) continue;
                StatNames[pair.Key] = Name(pair.Value.displayName, pair.Value.entryDisplayName, pair.Value._name);
                StatPercent[pair.Key] = pair.Value.isPercentStat;
            }
        });
        Step("currencies", () =>
        {
            foreach (var pair in db.GetCurrencies())
                if (pair.Value is not null) CurrencyNames[pair.Key] = Name(pair.Value.displayName, pair.Value.entryDisplayName, pair.Value._name);
        });
        Step("scenes", () =>
        {
            foreach (var pair in db.GetGameScenes())
            {
                var scene = pair.Value;
                if (scene?.adventureGuideBosses is null) continue;
                var label = Name(scene.displayName, scene.entryDisplayName, scene._name) + (scene.DungeonFinderEnabled ? " (dungeon)" : string.Empty);
                foreach (var boss in scene.adventureGuideBosses)
                {
                    if (boss is null) continue;
                    if (!bossScenes.TryGetValue(boss.npcID, out var list)) bossScenes[boss.npcID] = list = new List<string>();
                    if (!list.Contains(label)) list.Add(label);
                }
            }
        });
        Step("lootTables", () =>
        {
            foreach (var pair in db.GetLootTables())
            {
                var table = pair.Value;
                if (table is null) continue;
                LootTableNames[pair.Key] = Name(table.entryDisplayName, table.entryName);
                if (table.lootItems is null) continue;
                foreach (var entry in table.lootItems)
                {
                    if (entry is null) continue;
                    Push(DropsByItem, entry.itemID, new Drop(pair.Key, entry.dropRate, entry.min, entry.max));
                }
            }
        });
        Step("npcs", () =>
        {
            foreach (var pair in db.GetNPCs())
            {
                var npc = pair.Value;
                if (npc is null) continue;
                var name = Name(npc.displayName, npc.entryDisplayName, npc._name);
                npcNames[pair.Key] = name;
                if (npc.lootTables is null) continue;
                var type = npc.npcType;
                var rank = type switch
                {
                    RPGNpc.NPC_TYPE.BOSS => 4,
                    RPGNpc.NPC_TYPE.RARE => 3,
                    RPGNpc.NPC_TYPE.ELITE => 2,
                    _ => 1
                };
                var rankLabel = type switch
                {
                    RPGNpc.NPC_TYPE.BOSS => "Boss",
                    RPGNpc.NPC_TYPE.RARE => "Rare",
                    RPGNpc.NPC_TYPE.ELITE => "Elite",
                    _ => "Mob"
                };
                var where = bossScenes.TryGetValue(pair.Key, out var scenes) ? " in " + string.Join(" / ", scenes) : string.Empty;
                var levels = npc.MinLevel > 0 ? (npc.MaxLevel > npc.MinLevel ? $", lvl {npc.MinLevel}-{npc.MaxLevel}" : $", lvl {npc.MinLevel}") : string.Empty;
                foreach (var binding in npc.lootTables)
                {
                    if (binding is null) continue;
                    var tableChance = binding.dropRate is > 0f and < 100f ? $", table {Pct(binding.dropRate)}" : string.Empty;
                    Push(HoldersByLootTable, binding.lootTableID, new Holder($"{rankLabel} <b>{name}</b>{where}{levels}{tableChance}", rank));
                }
            }
        });
        Step("worldLoot", () =>
        {
            // Economy settings list global loot tables that apply to every NPC of at least
            // MinimumWorldLootRank (within an NPC level band): the game's open-world random drops.
            var economy = db.GetEconomySettings();
            if (economy?.WorldLootTables is null) return;
            var minRank = economy.MinimumWorldLootRank switch
            {
                RPGNpc.NPC_TYPE.BOSS => "boss",
                RPGNpc.NPC_TYPE.RARE => "rare",
                RPGNpc.NPC_TYPE.ELITE => "elite",
                _ => "normal"
            };
            foreach (var entry in economy.WorldLootTables)
            {
                if (entry is null) continue;
                var band = entry.MinNPCLevel > 0 || entry.MaxNPCLevel > 0
                    ? $", enemy lvl {Math.Max(1, entry.MinNPCLevel)}-{(entry.MaxNPCLevel > 0 ? entry.MaxNPCLevel.ToString() : "any")}"
                    : string.Empty;
                var chance = entry.dropRate is > 0f and < 100f ? $", table {Pct(entry.dropRate)}" : string.Empty;
                Push(HoldersByLootTable, entry.lootTableID,
                    new Holder($"<b>Open-world random drop</b> from any {minRank}-or-stronger enemy{band}{chance}", WorldLootRank));
                _worldLootTables++;
            }
        });
        Step("nodes", () =>
        {
            foreach (var pair in db.GetResources())
            {
                var node = pair.Value;
                if (node?.ranks is null) continue;
                var name = Name(node.displayName, node.entryDisplayName, node._name);
                foreach (var rank in node.ranks)
                    if (rank is not null && rank.lootTableID >= 0)
                        Push(HoldersByLootTable, rank.lootTableID, new Holder($"Gathered from <b>{name}</b>", 0));
            }
        });
        Step("vendors", () =>
        {
            var sellers = new Dictionary<int, List<string>>();
            foreach (var pair in db.GetNPCs())
            {
                var npc = pair.Value;
                if (npc is null) continue;
                var name = Name(npc.displayName, npc.entryDisplayName, npc._name);
                if (npc.merchantTableID >= 0) Push(sellers, npc.merchantTableID, name);
                if (npc.MerchantTables is null) continue;
                foreach (var t in npc.MerchantTables) if (t is not null) Push(sellers, t.MerchantTableID, name);
            }

            foreach (var pair in db.GetMerchantTables())
            {
                var table = pair.Value;
                if (table?.onSaleItems is null) continue;
                var who = sellers.TryGetValue(pair.Key, out var names) ? string.Join(", ", names.Distinct().Take(4)) : Name(table.entryDisplayName, table.entryName);
                foreach (var entry in table.onSaleItems)
                {
                    if (entry is null) continue;
                    var price = entry.cost > 0 ? $" - {entry.cost} {Currency(entry.currencyID)}" : string.Empty;
                    Push(VendorsByItem, entry.itemID, $"Sold by <b>{who}</b>{price}");
                }
            }
        });
        Step("recipes", () =>
        {
            var skills = new Dictionary<int, string>();
            foreach (var pair in db.GetSkills())
                if (pair.Value is not null) skills[pair.Key] = Name(pair.Value.displayName, pair.Value.entryDisplayName, pair.Value._name);
            foreach (var pair in db.GetRecipes())
            {
                var recipe = pair.Value;
                if (recipe?.ranks is null) continue;
                var name = Name(recipe.displayName, recipe.entryDisplayName, recipe._name);
                var skill = skills.TryGetValue(recipe.craftingSkillID, out var s) ? $" ({s})" : string.Empty;
                foreach (var rank in recipe.ranks)
                {
                    if (rank?.allCraftedItems is null) continue;
                    foreach (var crafted in rank.allCraftedItems)
                        if (crafted is not null) Push(RecipesByItem, crafted.craftedItemID, $"Crafted: <b>{name}</b>{skill}");
                }
            }
        });
        Step("quests", () =>
        {
            var tasks = new Dictionary<int, RPGTask>();
            foreach (var pair in db.GetTasks()) if (pair.Value is not null) tasks[pair.Key] = pair.Value;
            foreach (var pair in db.GetQuests())
            {
                var quest = pair.Value;
                if (quest is null) continue;
                var name = Name(quest.displayName, quest.entryDisplayName, quest._name);
                var chain = string.IsNullOrWhiteSpace(quest.QuestChainName) ? string.Empty : $" [{quest.QuestChainName}]";
                if (quest.itemsGiven is not null)
                    foreach (var e in quest.itemsGiven) if (e is not null) Push(QuestsByItem, e.itemID, $"Given by quest <b>{name}</b>{chain}");
                if (quest.rewardsGiven is not null)
                    foreach (var e in quest.rewardsGiven)
                        if (e is not null && e.rewardType == RPGQuest.QuestRewardType.item) Push(QuestsByItem, e.itemID, $"Reward of quest <b>{name}</b>{chain}");
                if (quest.rewardsToPick is not null)
                    foreach (var e in quest.rewardsToPick)
                        if (e is not null && e.rewardType == RPGQuest.QuestRewardType.item) Push(QuestsByItem, e.itemID, $"Choice reward of quest <b>{name}</b>{chain}");
                if (quest.objectives is null) continue;
                foreach (var objective in quest.objectives)
                {
                    if (objective is null || !tasks.TryGetValue(objective.taskID, out var task)) continue;
                    if (task.taskType == RPGTask.TASK_TYPE.getItem && task.itemToGetID >= 0)
                        Push(QuestsByItem, task.itemToGetID, $"Needed for quest <b>{name}</b>{chain}" + (task.taskValue > 1 ? $" (collect {task.taskValue})" : string.Empty));
                    else if (task.taskType == RPGTask.TASK_TYPE.useItem && task.itemToUseID >= 0)
                        Push(QuestsByItem, task.itemToUseID, $"Used in quest <b>{name}</b>{chain}");
                }
            }
        });
        Step("start", () =>
        {
            foreach (var pair in db.GetClasses())
            {
                if (pair.Value?.startItems is null) continue;
                var name = Name(pair.Value.displayName, pair.Value.entryDisplayName, pair.Value._name);
                foreach (var e in pair.Value.startItems) if (e is not null) Push(StartsByItem, e.itemID, $"Starting gear: {name}");
            }

            foreach (var pair in db.GetRaces())
            {
                if (pair.Value?.startItems is null) continue;
                var name = Name(pair.Value.displayName, pair.Value.entryDisplayName, pair.Value._name);
                foreach (var e in pair.Value.startItems) if (e is not null) Push(StartsByItem, e.itemID, $"Starting item: {name}");
            }
        });
        Step("actionTargets", () =>
        {
            Dictionary<int, string> Names<T>(Il2CppSystem.Collections.Generic.Dictionary<int, T> source, Func<T, string> name) where T : RPGBuilderDatabaseEntry
            {
                var map = new Dictionary<int, string>();
                foreach (var pair in source) if (pair.Value is not null) map[pair.Key] = name(pair.Value);
                return map;
            }

            ActionTargets["Item"] = Names(db.GetItems(), e => Name(e.displayName, e.entryDisplayName, e._name));
            ActionTargets["Currency"] = Names(db.GetCurrencies(), e => Name(e.displayName, e.entryDisplayName, e._name));
            ActionTargets["Point"] = Names(db.GetPoints(), e => Name(e._displayName, e.entryDisplayName, e._name));
            ActionTargets["Faction"] = Names(db.GetFactions(), e => Name(e.displayName, e.entryDisplayName, e._name));
            ActionTargets["Effect"] = Names(db.GetEffects(), e => Name(e.displayName, e.entryDisplayName, e._name));
            ActionTargets["Skill"] = Names(db.GetSkills(), e => Name(e.displayName, e.entryDisplayName, e._name));
            ActionTargets["Ability"] = Names(db.GetAbilities(), e => Name(e.entryDisplayName, e.entryName));
            ActionTargets["Recipe"] = Names(db.GetRecipes(), e => Name(e.displayName, e.entryDisplayName, e._name));
            foreach (var pair in db.GetEffects()) if (pair.Value is not null) EffectsById[pair.Key] = pair.Value;
            foreach (var pair in db.GetNPCs()) if (pair.Value is not null) NpcNames[pair.Key] = Name(pair.Value.displayName, pair.Value.entryDisplayName, pair.Value._name);
        });
        Step("corruption", () =>
        {
            var settings = db.GetCombatSettings();
            if (settings is not null)
            {
                if (settings.MaxCorruptionLevel > 0) MaxCorruptionLevel = settings.MaxCorruptionLevel;
                CorruptionAllStatsPercentPerLevel = settings.CorruptionGearAllStatsPercentPerLevel;
            }

            foreach (var pair in db.GetItems())
                if (pair.Value is not null && pair.Value.IsCorruptionToken) CorruptionTokens.Add(pair.Key);
        });

        Ready = true;
        log?.LogInfo($"Item details index: {string.Join(", ", report)}; drops for {DropsByItem.Count} items, "
            + $"{HoldersByLootTable.Count} loot tables bound ({_worldLootTables} open-world), max gear corruption {MaxCorruptionLevel} "
            + $"(+{CorruptionAllStatsPercentPerLevel}% all stats/level), corruption tokens: {CorruptionTokens.Count}.");
    }

    public static bool IsCorruptionToken(int itemId) => CorruptionTokens.Contains(itemId);

    /// <summary>What a consumable does when used: its GameActions (own list or shared template).</summary>
    public static string OnUseText(RPGItem item, string bullet)
    {
        var actions = item.UseGameActionsTemplate && item.GameActionsTemplate is not null ? item.GameActionsTemplate.GameActions : item.GameActions;
        var sb = new StringBuilder();
        if (item.actionAbilities is not null)
            foreach (var use in item.actionAbilities)
                if (use is not null && use.abilityID >= 0)
                    sb.Append(bullet).Append("Uses ability: <b>").Append(ActionTargets.TryGetValue("Ability", out var abilities) && abilities.TryGetValue(use.abilityID, out var an) ? an : $"#{use.abilityID}").Append("</b>\n");
        if (actions is null || actions.Count == 0) return sb.ToString().TrimEnd();
        foreach (var action in actions)
        {
            if (action is null) continue;
            var type = action.type.ToString();
            var id = type switch
            {
                "Item" => action.ItemID, "Currency" => action.CurrencyID, "Point" => action.PointID, "Faction" => action.FactionID,
                "Effect" => action.EffectID, "Skill" => action.SkillID, "Ability" => action.AbilityID, "Recipe" => action.RecipeID,
                "LootTable" => action.LootTableID, _ => -1
            };
            var target = type == "LootTable" ? LootName(id)
                : id >= 0 && ActionTargets.TryGetValue(type, out var names) && names.TryGetValue(id, out var n) ? n : string.Empty;
            var line = type switch
            {
                "Effect" => $"<b>{target}</b>: {EffectDetail(action.EffectID)}",
                "Item" => action.AlterAction == GameActionsData.AlterAction.Remove
                    ? (action.ItemID == item.ID ? (action.Amount <= 1 ? "Single use" : $"Uses {action.Amount} per use") : $"Consumes {Math.Max(1, action.Amount)} x <b>{target}</b>")
                    : $"Gives {Math.Max(1, action.Amount)} x <b>{target}</b>",
                "Currency" => $"{(action.AlterAction == GameActionsData.AlterAction.Remove ? "Costs" : "Gives")} {action.Amount} <b>{target}</b>",
                "Point" => $"{(action.AlterAction == GameActionsData.AlterAction.Remove ? "Removes" : "Gives")} {action.Amount} <b>{target}</b> point(s)",
                "Faction" => $"{(action.AlterAction == GameActionsData.AlterAction.Remove ? "Loses" : "Gains")} {action.Amount} reputation with <b>{target}</b>",
                "Skill" => $"<b>{target}</b> skill: +{action.Amount} XP",
                "Ability" => $"Teaches/uses ability <b>{target}</b>",
                "Recipe" => $"Teaches recipe <b>{target}</b>",
                "LootTable" => $"Opens loot table <b>{target}</b>",
                "TriggerSound" or "TriggerAnimation" or "TriggerVisualEffect" => null,
                _ => type + (string.IsNullOrEmpty(target) ? string.Empty : $": <b>{target}</b>") + (action.Amount != 0 ? $" x{action.Amount}" : string.Empty)
            };
            if (line is null) continue; // cosmetic actions
            sb.Append(bullet).Append(line);
            if (action.chance is > 0f and < 100f) sb.Append($" ({Pct(action.chance)} chance)");
            sb.Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Stat lines for the preview, optionally showing the bonus a corruption level adds.</summary>
    public static string StatsText(RPGItem item, int corruptionLevel, string bullet, bool isArmorOrEquipment)
    {
        var sb = new StringBuilder();
        var type = Name(item.ItemType?.entryDisplayName, item.itemType);
        // Unused slot references are not null on every item (a sword reported ArmorSlot "BELT"), so pick
        // the weapon fields for weapons and the armor fields only for items that have an armor type/slot.
        var isWeapon = item.maxDamage > 0 || (type?.IndexOf("weapon", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        // Materials also carry default armor fields ("CLOTH - BELT"); only gear shows them.
        var armorType = isArmorOrEquipment ? Name(item.ArmorType?.entryDisplayName, item.armorType) : string.Empty;
        var sub = isWeapon ? Name(item.WeaponType?.entryDisplayName, item.weaponType) : armorType;
        var slot = isWeapon
            ? Name(item.WeaponSlot?.entryDisplayName, item.slotType)
            : isArmorOrEquipment
                ? Name(item.equipmentSlot, item.ArmorSlot?.entryDisplayName)
                : string.Empty;
        sb.Append("<smallcaps><color=#9fb8ad>").Append(type);
        if (!string.IsNullOrEmpty(sub)) sb.Append(" - ").Append(sub);
        if (!string.IsNullOrEmpty(slot)) sb.Append(" - ").Append(slot);
        sb.Append("</color></smallcaps>\n");

        if (item.maxDamage > 0)
        {
            sb.Append($"<b>{item.minDamage}-{item.maxDamage}</b> damage");
            if (item.AttackSpeed > 0f) sb.Append($"   {item.AttackSpeed.ToString("0.##", CultureInfo.InvariantCulture)} speed");
            sb.Append('\n');
        }

        if (item.stats is not null)
        {
            foreach (var stat in item.stats)
            {
                if (stat is null) continue;
                var pct = stat.isPercent || (StatPercent.TryGetValue(stat.statID, out var p) && p);
                sb.Append("<color=#f3dc96>").Append(Signed(stat.amount, pct)).Append("</color><pos=30%>").Append(StatName(stat.statID));
                if (corruptionLevel > 0)
                {
                    var extra = SafeExtra(stat.statID, stat.amount, corruptionLevel);
                    if (Math.Abs(extra) > 0.0001f) sb.Append($" <color=#c084fc>({Signed(extra, pct)} corruption)</color>");
                }

                sb.Append('\n');
            }
        }

        if (item.randomStats is not null && item.randomStats.Count > 0)
        {
            sb.Append($"<color=#9aa3ad>Random stats (up to {item.randomStatsMax}):</color>\n");
            foreach (var r in item.randomStats)
            {
                if (r is null) continue;
                var range = r.isPercent ? $"{Num(r.minValue)}-{Num(r.maxValue)}%" : $"{Num(r.minValue)}-{Num(r.maxValue)}";
                sb.Append("<color=#f3dc96>+").Append(range).Append("</color><pos=30%>").Append(StatName(r.statID));
                if (r.chance is > 0f and < 100f) sb.Append($" <color=#9aa3ad>({Pct(r.chance)})</color>");
                sb.Append('\n');
            }
        }

        if (item.sockets is not null && item.sockets.Count > 0)
        {
            // Each socket's gem type (e.g. "Red Gem"), coloured by the colour word in its name.
            var names = new List<string>();
            foreach (var socket in item.sockets)
            {
                if (socket is null) continue;
                var label = Name(socket.GemSocketType?.entryDisplayName, socket.GemSocketType?.entryName, socket.socketType);
                if (string.IsNullOrEmpty(label)) label = "Any gem";
                names.Add($"<nobr><color={SocketColor(label)}>● {label}</color></nobr>");
            }

            sb.Append(names.Count == 1 ? "Socket: " : $"{names.Count} sockets: ").Append(string.Join("   ", names)).Append('\n');
        }
        if (corruptionLevel > 0)
            sb.Append($"<color=#c084fc>Corruption +{corruptionLevel}"
                + (CorruptionAllStatsPercentPerLevel > 0f ? $" (+{Num(CorruptionAllStatsPercentPerLevel * corruptionLevel)}% all stats)" : string.Empty)
                + "</color>\n");
        var required = SafeLevelRequirement(item);
        if (required > 0) sb.Append($"<color=#e07a6a>Requires Level {required}</color>\n");
        if (item.sellPrice > 0) sb.Append($"<color=#9aa3ad>Sells for {item.sellPrice} {Currency(item.sellCurrencyID)}</color>\n");
        return sb.ToString().TrimEnd();
    }

    public static string SourcesText(int itemId, bool dropInWorld, string bullet)
    {
        var lines = new List<(int Order, string Text)>();
        if (DropsByItem.TryGetValue(itemId, out var drops))
        {
            foreach (var drop in drops)
            {
                var rate = drop.ItemRate is > 0f and < 100f ? $" ({Pct(drop.ItemRate)})" : string.Empty;
                var count = drop.Max > 1 ? $" x{drop.Min}-{drop.Max}" : string.Empty;
                if (HoldersByLootTable.TryGetValue(drop.LootTableId, out var holders))
                {
                    foreach (var h in holders.OrderByDescending(h => h.Rank).Take(8))
                        lines.Add((10 - h.Rank, (h.Rank is >= 1 and <= 4 ? "Dropped by " + h.Text : h.Text) + rate + count));
                    if (holders.Count > 8) lines.Add((9, $"...and {holders.Count - 8} more from loot table {LootName(drop.LootTableId)}"));
                }
                else
                {
                    lines.Add((8, $"Loot table <b>{LootName(drop.LootTableId)}</b>{rate}{count} <color=#9aa3ad>(chest or scripted reward)</color>"));
                }
            }
        }

        Add(lines, 20, VendorsByItem, itemId);
        Add(lines, 30, RecipesByItem, itemId);
        Add(lines, 5, QuestsByItem, itemId);
        Add(lines, 40, StartsByItem, itemId);
        if (lines.Count == 0)
            return "<color=#f0b84d>No listed source.</color> Not dropped, sold, crafted or rewarded by any table in the game database: likely a developer/test record or an item handed out only by world objects or scripts.";

        var sb = new StringBuilder();
        foreach (var line in lines.OrderBy(l => l.Order).Select(l => l.Text).Distinct().Take(24))
            sb.Append(bullet).Append(line).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>Plain-language summary of an effect's first rank: what it changes, how much, for how long.</summary>
    public static string EffectDetail(int effectId)
    {
        if (!EffectsById.TryGetValue(effectId, out var effect)) return "unknown effect";
        try
        {
            var rank = effect.ranks is not null && effect.ranks.Count > 0 ? effect.ranks[0] : null;
            var duration = effect.endless ? " (permanent)" : effect.duration > 0f ? $" for {Num(effect.duration)}s" : string.Empty;
            var ticks = effect.pulses > 1 ? $" in {effect.pulses} ticks" : string.Empty;
            string StatOf(int id) => id >= 0 ? StatName(id) : "Health";
            switch (effect.effectType)
            {
                case RPGEffect.EFFECT_TYPE.Stat:
                {
                    var parts = new List<string>();
                    if (rank?.statEffectsData is not null)
                        foreach (var s in rank.statEffectsData)
                            if (s is not null) parts.Add($"{Signed(s.statEffectModification, s.isPercent)} {StatName(s.statID)}");
                    return (parts.Count == 0 ? "changes stats" : string.Join(", ", parts)) + duration;
                }
                case RPGEffect.EFFECT_TYPE.InstantHeal:
                    return $"restores {rank?.Damage ?? 0} {StatOf(rank?.alteredStatID ?? -1)}";
                case RPGEffect.EFFECT_TYPE.HealOverTime:
                    return $"restores {rank?.Damage ?? 0} {StatOf(rank?.alteredStatID ?? -1)} per tick{ticks}{duration}";
                case RPGEffect.EFFECT_TYPE.InstantDamage:
                    return $"deals {rank?.Damage ?? 0} damage";
                case RPGEffect.EFFECT_TYPE.DamageOverTime:
                    return $"deals {rank?.Damage ?? 0} damage per tick{ticks}{duration}";
                case RPGEffect.EFFECT_TYPE.Pet:
                    return $"summons {(rank is not null && NpcNames.TryGetValue(rank.petNPCDataID, out var pet) ? pet : "a pet")}"
                        + (rank is not null && rank.petDuration > 0f ? $" for {Num(rank.petDuration)}s" : string.Empty);
                case RPGEffect.EFFECT_TYPE.RollLootTable:
                    return $"opens loot table {(rank is not null ? LootName(rank.lootTableID) : "?")}";
                case RPGEffect.EFFECT_TYPE.Mount:
                    return "summons a mount";
                case RPGEffect.EFFECT_TYPE.Teleport:
                    return "teleports you";
                case RPGEffect.EFFECT_TYPE.Resurrect:
                    return "resurrects";
                case RPGEffect.EFFECT_TYPE.Dispel:
                    return "removes effects";
                default:
                    return effect.effectType.ToString().ToLowerInvariant() + duration;
            }
        }
        catch
        {
            return "effect";
        }
    }

    private static string SocketColor(string label)
    {
        var l = label.ToLowerInvariant();
        if (l.Contains("red")) return "#ff5a5a";
        if (l.Contains("blue")) return "#5aa8ff";
        if (l.Contains("green")) return "#5aff7a";
        if (l.Contains("yellow")) return "#ffd84a";
        if (l.Contains("purple") || l.Contains("violet")) return "#c07aff";
        if (l.Contains("orange")) return "#ff9a3c";
        if (l.Contains("white") || l.Contains("prismatic")) return "#f2f2f2";
        return "#cfd6e6";
    }

    private static void Add(List<(int, string)> lines, int order, Dictionary<int, List<string>> map, int itemId)
    {
        if (!map.TryGetValue(itemId, out var list)) return;
        foreach (var text in list.Distinct()) lines.Add((order, text));
    }

    /// <summary>The game's own level requirement (EconomyUtilities.GetItemLevelRequirement), as its tooltip shows.</summary>
    private static int SafeLevelRequirement(RPGItem item)
    {
        try
        {
            return EconomyUtilities.GetItemLevelRequirement(item);
        }
        catch
        {
            return 0;
        }
    }

    private static float SafeExtra(int statId, float amount, int level)
    {
        try
        {
            return BLINK.RPGBuilder.World.CorruptionGearBonus.Extra(statId, amount, level);
        }
        catch
        {
            return 0f;
        }
    }

    private static void Push<T>(Dictionary<int, List<T>> map, int key, T value)
    {
        if (key < 0) return;
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<T>();
        list.Add(value);
    }

    private static string StatName(int id) => StatNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : $"stat {id}";
    private static string Currency(int id) => CurrencyNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : "currency";
    private static string LootName(int id) => LootTableNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : $"#{id}";
    private static string Num(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Pct(float v) => Num(v) + "%";
    private static string Signed(float v, bool pct) => (v >= 0 ? "+" : string.Empty) + Num(v) + (pct ? "%" : string.Empty);

    private static string Name(params string[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? string.Empty;
}
