using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Timeline.Epochs;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2Headless;

/// <summary>
/// Parity-oracle commands so spire-mentor's C#-vs-Rust parity harness (sts2-pycli, pinned 0.107.1) can use
/// this 0.111.0 build as its C# oracle. Ported from spire-mentor sts2-pycli/src/Sts2Headless/RunSimulator.cs
/// (6ca85c1). Additive only: no existing command's behaviour or output changes.
/// </summary>
public partial class RunSimulator
{
    /// <summary>
    /// Legal in-combat actions. Port of LegalActionsImpl: one play_card per CanPlay card (AnyEnemy cards fan
    /// out over the alive-enemy subset, the same index space play_card uses), end_turn, and one use_potion per
    /// occupied non-Automatic slot with a null target.
    /// </summary>
    public Dictionary<string, object?> LegalActions()
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];
            var pcs = player.PlayerCombatState;
            if (pcs?.Hand == null) return Error("Not in combat");

            var hand = pcs.Hand.Cards;
            var combatState = CombatManager.Instance.DebugOnlyGetState();
            var aliveCount = combatState?.Enemies?.Count(e => e != null && e.IsAlive) ?? 0;

            var actions = new List<Dictionary<string, object?>>();
            for (int i = 0; i < hand.Count; i++)
            {
                var card = hand[i];
                if (!card.CanPlay(out _, out _)) continue;
                if (card.TargetType == TargetType.AnyEnemy && aliveCount > 0)
                {
                    for (int t = 0; t < aliveCount; t++)
                        actions.Add(new Dictionary<string, object?>
                        {
                            ["action"] = "play_card", ["card_index"] = i, ["target_index"] = t,
                        });
                }
                else
                {
                    actions.Add(new Dictionary<string, object?>
                    {
                        ["action"] = "play_card", ["card_index"] = i, ["target_index"] = null,
                    });
                }
            }
            actions.Add(new Dictionary<string, object?> { ["action"] = "end_turn" });

            var potions = player.Potions?.ToList() ?? new();
            for (int i = 0; i < potions.Count; i++)
            {
                if (potions[i] == null) continue;
                // Automatic potions (Fairy in a Bottle) can never be drunk manually.
                if (potions[i].Usage == MegaCrit.Sts2.Core.Entities.Potions.PotionUsage.Automatic) continue;
                actions.Add(new Dictionary<string, object?>
                {
                    ["action"] = "use_potion", ["potion_index"] = i, ["target_index"] = null,
                });
            }

            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["actions"] = actions,
                ["energy"] = pcs.Energy,
                ["hand_size"] = hand.Count,
                ["alive_enemy_count"] = aliveCount,
            };
        }
        catch (Exception ex) { return ErrorWithTrace("LegalActions failed", ex); }
    }

    /// <summary>
    /// Canonical-id combat snapshot. Port of GetStateImpl: all monster slots (dead ones with alive=false),
    /// ordered pile id lists (draw pile in internal order), resolved hand costs, sorted relic ids, RNG
    /// counters, orbs and Osty.
    /// </summary>
    public Dictionary<string, object?> GetState()
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];
            var pcs = player.PlayerCombatState;
            var combatState = CombatManager.Instance.DebugOnlyGetState();

            bool runGameOver = false;
            try { runGameOver = RunManager.Instance.IsGameOver; }
            catch (NullReferenceException) { }
            var combatTerminal = pcs == null || runGameOver || (CombatManager.Instance?.IsOverOrEnding ?? false);

            var playerPowers = player.Creature?.Powers?.Select(pw => new Dictionary<string, object?>
            {
                ["kind"] = pw.Id.Entry,
                ["amount"] = pw.Amount,
                ["display_amount"] = pw.DisplayAmount,
            }).ToList() ?? new List<Dictionary<string, object?>>();

            var playerDict = new Dictionary<string, object?>
            {
                ["hp"] = player.Creature?.CurrentHp ?? 0,
                ["max_hp"] = player.Creature?.MaxHp ?? 0,
                ["block"] = player.Creature?.Block ?? 0,
                ["energy"] = pcs?.Energy ?? 0,
                ["powers"] = playerPowers,
            };

            var monsters = new List<Dictionary<string, object?>>();
            if (combatState?.Enemies != null)
            {
                for (int i = 0; i < combatState.Enemies.Count; i++)
                {
                    var e = combatState.Enemies[i];
                    if (e == null)
                    {
                        monsters.Add(new Dictionary<string, object?>
                        {
                            ["uid"] = i, ["kind"] = null, ["hp"] = 0, ["max_hp"] = 0, ["block"] = 0,
                            ["alive"] = false, ["powers"] = new List<Dictionary<string, object?>>(),
                        });
                        continue;
                    }
                    var ePowers = e.Powers?.Select(pw => new Dictionary<string, object?>
                    {
                        ["kind"] = pw.Id.Entry,
                        ["amount"] = pw.Amount,
                        ["display_amount"] = pw.DisplayAmount,
                    }).ToList() ?? new List<Dictionary<string, object?>>();
                    monsters.Add(new Dictionary<string, object?>
                    {
                        ["uid"] = i,
                        ["kind"] = e.Monster?.Id.Entry,
                        ["hp"] = e.CurrentHp,
                        ["max_hp"] = e.MaxHp,
                        ["block"] = e.Block,
                        ["alive"] = e.IsAlive,
                        ["powers"] = ePowers,
                        ["move_state"] = e.Monster?.NextMove?.Id,
                    });
                }
            }

            var relics = player.Relics?
                .Select(r => r.Id.Entry)
                .Where(s => !string.IsNullOrEmpty(s))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList() ?? new List<string>();

            object? playerWon = null;
            if (combatTerminal)
                playerWon = player.Creature == null ? (object?)false : (object?)!player.Creature.IsDead;

            static List<string>? Ids(IEnumerable<CardModel>? cards) => cards?.Select(c => c.Id.ToString()).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["player"] = playerDict,
                ["monsters"] = monsters,
                ["hand_count"] = pcs?.Hand?.Cards?.Count ?? 0,
                ["hand_ids"] = Ids(pcs?.Hand?.Cards),
                ["hand_costs"] = pcs?.Hand?.Cards?.Select(c => c.EnergyCost?.GetResolved() ?? 0).ToList(),
                ["draw_count"] = pcs?.DrawPile?.Cards?.Count ?? 0,
                ["draw_ids"] = Ids(pcs?.DrawPile?.Cards),
                ["discard_count"] = pcs?.DiscardPile?.Cards?.Count ?? 0,
                ["discard_ids"] = Ids(pcs?.DiscardPile?.Cards),
                ["exhaust_count"] = pcs?.ExhaustPile?.Cards?.Count ?? 0,
                ["exhaust_ids"] = Ids(pcs?.ExhaustPile?.Cards),
                ["play_ids"] = Ids(pcs?.PlayPile?.Cards),
                ["turn"] = combatState?.RoundNumber ?? 0,
                ["relics"] = relics,
                ["combat_terminal"] = combatTerminal,
                ["player_won"] = playerWon,
                ["rng_counters"] = RngCounters(),
                ["orb_slots"] = pcs?.OrbQueue?.Capacity ?? 0,
                ["orbs"] = pcs?.OrbQueue?.Orbs?.Select(o => new Dictionary<string, object?>
                {
                    ["type"] = o.GetType().Name.Replace("Orb", ""),
                    ["passive"] = (int)o.PassiveVal,
                    ["evoke"] = (int)o.EvokeVal,
                }).ToList(),
                ["osty"] = player.Osty is { } osty
                    ? new Dictionary<string, object?>
                    {
                        ["hp"] = osty.CurrentHp, ["max_hp"] = osty.MaxHp, ["block"] = osty.Block, ["alive"] = osty.IsAlive,
                    }
                    : (player.Character?.Id.Entry == "NECROBINDER"
                        ? new Dictionary<string, object?> { ["alive"] = false }
                        : null),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("GetState failed", ex); }
    }

    // 0.111.0 dropped the public Rng.Counter; the serializable snapshot still carries the read count.
    private static int Ctr(Rng r) => r.ToSerializable().counter;

    /// <summary>Per-stream RNG read counts keyed like the Rust dump's rng_counters (RunRngType snake_case).</summary>
    private Dictionary<string, object?> RngCounters()
    {
        var d = new Dictionary<string, object?>();
        var rng = _runState?.Rng;
        if (rng == null) return d;
        try
        {
            d["up_front"] = Ctr(rng.UpFront);
            d["shuffle"] = Ctr(rng.Shuffle);
            d["combat_card_generation"] = Ctr(rng.CombatCardGeneration);
            d["combat_potion_generation"] = Ctr(rng.CombatPotionGeneration);
            d["combat_card_selection"] = Ctr(rng.CombatCardSelection);
            d["combat_energy_costs"] = Ctr(rng.CombatEnergyCosts);
            d["combat_targets"] = Ctr(rng.CombatTargets);
            d["monster_ai"] = Ctr(rng.MonsterAi);
            d["niche"] = Ctr(rng.Niche);
            d["combat_orbs"] = Ctr(rng.CombatOrbGeneration);
            var prng = (_runState?.Players != null && _runState.Players.Count > 0) ? _runState.Players[0].PlayerRng : null;
            if (prng != null)
            {
                d["rewards"] = Ctr(prng.Rewards);
                d["shops"] = Ctr(prng.Shops);
                d["transformations"] = Ctr(prng.Transformations);
            }
        }
        catch { }
        return d;
    }

    /// <summary>
    /// STS2_ORACLE=1 switches on the spire-mentor oracle conventions that differ from this headless' defaults:
    /// typed-seed canonicalization (SeedHelper.CanonicalizeSeed) and use_potion with a null target on an
    /// AnyEnemy potion resolving to the first alive enemy. Unset = previous behaviour.
    /// </summary>
    internal static readonly bool OracleMode = Environment.GetEnvironmentVariable("STS2_ORACLE") == "1";

    /// <summary>
    /// Seed as the run will use it. Oracle mode canonicalizes like the real game's typed-seed path; then an
    /// optional STS2_ORACLE_SEED_PREFIX is prepended (diagnostic: "old" selects 0.111.0's legacy 32-bit seed
    /// hash in RunRngSet). Outside oracle mode the seed is returned unchanged.
    /// </summary>
    private static string? OracleSeed(string? seed)
    {
        if (seed == null || !OracleMode) return seed;
        var s = SeedHelper.CanonicalizeSeed(seed);
        var prefix = Environment.GetEnvironmentVariable("STS2_ORACLE_SEED_PREFIX");
        return string.IsNullOrEmpty(prefix) ? s : prefix + s;
    }

    /// <summary>
    /// Player UnlockState. Default UnlockState.all (unchanged). played_save mimics a played profile where
    /// NEOW_EPOCH is obtained but never revealed (no Neow altar); port of spire-mentor BuildUnlockState.
    /// </summary>
    private static UnlockState BuildUnlockState(bool playedSave)
    {
        if (!playedSave)
            return UnlockState.all;
        SaveManager.Instance.ObtainEpochOverride(EpochModel.GetId<NeowEpoch>(), EpochState.Obtained);
        return SaveManager.Instance.GenerateUnlockStateFromProgress();
    }

    /// <summary>The lobby's act_selection stream (0.111.0 StartRunLobby.cs:437: 64-bit seed hash).</summary>
    private static Rng ActSelectionRng(string seedStr) =>
        new Rng(StringHelper.GetDeterministicHashCode(seedStr), "act_selection");

    private static ActModel? ResolveAct1(string act1, string seedStr, bool playedSave) =>
        act1.ToLowerInvariant() switch
        {
            "overgrowth" => ModelDb.Act<Overgrowth>(),
            "underdocks" => ModelDb.Act<Underdocks>(),
            "roll" => ActModel.GetRandomList(ActSelectionRng(seedStr), BuildUnlockState(playedSave), isMultiplayer: false).First(),
            _ => null,
        };

    /// <summary>Seed-driven act roll without starting a run (port of RollActsImpl).</summary>
    public Dictionary<string, object?> RollActs(string? seed, bool playedSave = false)
    {
        try
        {
            EnsureModelDbInitialized();
            var seedStr = SeedHelper.CanonicalizeSeed(seed ?? "");
            var unlockState = BuildUnlockState(playedSave);
            var acts = ActModel.GetRandomList(ActSelectionRng(seedStr), unlockState, isMultiplayer: false)
                .Select(a => a.Id.Entry).ToList();
            return new Dictionary<string, object?>
            {
                ["type"] = "acts",
                ["seed"] = seedStr,
                ["acts"] = acts,
                ["underdocks_unlocked"] = unlockState.IsEpochRevealed<UnderdocksEpoch>(),
                ["test_mode"] = TestMode.IsOn,
            };
        }
        catch (Exception ex) { return ErrorWithTrace("RollActs failed", ex); }
    }

    // ─── dump_* (ports of spire-mentor DumpRooms/DumpMaps/DumpCardReward/DumpRelicReward/DumpShop/DumpContent/DumpEvents) ───

    private static object? GetInheritedField(object obj, string fieldName)
    {
        var t = obj.GetType();
        while (t != null)
        {
            var f = t.GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (f != null) return f.GetValue(obj);
            t = t.BaseType;
        }
        return null;
    }

    /// <summary>Each act's pre-generated RoomSet (encounters, boss, shuffled event list) as Id.Entry strings.</summary>
    public Dictionary<string, object?> DumpRooms()
    {
        if (_runState == null) return Error("No run in progress");
        var acts = new List<object?>();
        foreach (var act in _runState.Acts)
        {
            if (GetInheritedField(act, "_rooms") is not RoomSet roomSet) continue;
            static List<string> Ids(List<EncounterModel> xs) => xs.Select(e => e?.Id.Entry ?? "?").ToList();
            acts.Add(new Dictionary<string, object?>
            {
                ["normal"] = Ids(roomSet.normalEncounters),
                ["elite"] = Ids(roomSet.eliteEncounters),
                ["boss"] = roomSet.Boss?.Id.Entry,
                ["events"] = roomSet.events.Select(e => e?.Id.Entry ?? "?").ToList(),
                ["events_visited"] = roomSet.eventsVisited,
            });
        }
        return new Dictionary<string, object?> { ["type"] = "rooms", ["acts"] = acts };
    }

    /// <summary>Every act's map graph rebuilt from Rng(run seed, "act_N_map") without advancing the run.</summary>
    public Dictionary<string, object?> DumpMaps()
    {
        if (_runState == null) return Error("No run in progress");
        var actList = _runState.Acts.ToList();
        bool isMultiplayer = _runState.Players.Count > 1;
        var acts = new List<object?>();
        for (int i = 0; i < actList.Count; i++)
        {
            var act = actList[i];
            var rng = new Rng(_runState.Rng.Seed, $"act_{i + 1}_map");
            var map = new StandardActMap(rng, act, isMultiplayer, shouldReplaceTreasureWithElites: false, act.HasSecondBoss);
            acts.Add(SerializeMapGraph(map));
        }
        return new Dictionary<string, object?> { ["type"] = "maps", ["acts"] = acts };
    }

    private static Dictionary<string, object?> SerializeMapGraph(ActMap map)
    {
        var rows = new List<List<Dictionary<string, object?>>>();
        for (int row = 0; row < map.GetRowCount(); row++)
        {
            var rowNodes = new List<Dictionary<string, object?>>();
            foreach (var point in map.GetPointsInRow(row))
            {
                if (point == null) continue;
                rowNodes.Add(new Dictionary<string, object?>
                {
                    ["col"] = (int)point.coord.col,
                    ["row"] = (int)point.coord.row,
                    ["type"] = point.PointType.ToString(),
                    ["children"] = point.Children?.Select(ch => new Dictionary<string, object?>
                    {
                        ["col"] = (int)ch.coord.col,
                        ["row"] = (int)ch.coord.row,
                    }).ToList(),
                });
            }
            if (rowNodes.Count > 0) rows.Add(rowNodes);
        }
        var boss = new Dictionary<string, object?>
        {
            ["col"] = (int)map.BossMapPoint.coord.col,
            ["row"] = (int)map.BossMapPoint.coord.row,
            ["type"] = map.BossMapPoint.PointType.ToString(),
        };
        return new Dictionary<string, object?> { ["rows"] = rows, ["boss"] = boss };
    }

    /// <summary>One card reward rolled off PlayerRng.Rewards as a room victory would (CardFactory.CreateForReward).</summary>
    public Dictionary<string, object?> DumpCardReward(int count = 3, string roomType = "Monster")
    {
        if (_runState == null) return Error("No run in progress");
        var player = _runState.Players[0];
        var rt = roomType switch { "Elite" => RoomType.Elite, "Boss" => RoomType.Boss, _ => RoomType.Monster };
        var results = MegaCrit.Sts2.Core.Factories.CardFactory
            .CreateForReward(player, count, CardCreationOptions.ForRoom(player, rt)).ToList();
        var cards = results.Select(r => (object?)new Dictionary<string, object?>
        {
            ["id"] = r.Card.Id.Entry,
            ["upgraded"] = r.Card.IsUpgraded,
        }).ToList();
        return new Dictionary<string, object?> { ["type"] = "card_reward", ["cards"] = cards };
    }

    /// <summary>Consecutive relic drops: rarity roll off Rewards + front of that rarity's grab bag.</summary>
    public Dictionary<string, object?> DumpRelicReward(int count = 1)
    {
        if (_runState == null) return Error("No run in progress");
        var player = _runState.Players[0];
        var relics = new List<object?>();
        for (int i = 0; i < count; i++)
        {
            var relic = MegaCrit.Sts2.Core.Factories.RelicFactory.PullNextRelicFromFront(player);
            relics.Add(new Dictionary<string, object?> { ["id"] = relic.Id.Entry });
        }
        return new Dictionary<string, object?> { ["type"] = "relic_reward", ["relics"] = relics };
    }

    /// <summary>A full normal-merchant inventory off the player's Shops/Rewards streams, with read counts.</summary>
    public Dictionary<string, object?> DumpShop()
    {
        if (_runState == null) return Error("No run in progress");
        var player = _runState.Players[0];
        int shops0 = Ctr(player.PlayerRng.Shops);
        int rewards0 = Ctr(player.PlayerRng.Rewards);
        var inv = MerchantInventory.CreateForNormalMerchant(player);
        int shopsReads = Ctr(player.PlayerRng.Shops) - shops0;
        int rewardsReads = Ctr(player.PlayerRng.Rewards) - rewards0;
        return new Dictionary<string, object?>
        {
            ["type"] = "shop",
            ["cards"] = inv.CharacterCardEntries.Select(e => (object?)new Dictionary<string, object?>
            {
                ["id"] = e.CreationResult?.Card.Id.Entry,
                ["upgraded"] = e.CreationResult?.Card.IsUpgraded ?? false,
                ["on_sale"] = e.IsOnSale,
                ["price"] = e.Cost,
            }).ToList(),
            ["colorless"] = inv.ColorlessCardEntries.Select(e => (object?)new Dictionary<string, object?>
            {
                ["id"] = e.CreationResult?.Card.Id.Entry,
                ["upgraded"] = e.CreationResult?.Card.IsUpgraded ?? false,
                ["price"] = e.Cost,
            }).ToList(),
            ["relics"] = inv.RelicEntries.Select(e => (object?)new Dictionary<string, object?>
            {
                ["id"] = e.Model?.Id.Entry, ["price"] = e.Cost,
            }).ToList(),
            ["potions"] = inv.PotionEntries.Select(e => (object?)new Dictionary<string, object?>
            {
                ["id"] = e.Model?.Id.Entry, ["price"] = e.Cost,
            }).ToList(),
            ["removal_cost"] = inv.CardRemovalEntry?.Cost,
            ["shops_reads"] = shopsReads,
            ["rewards_reads"] = rewardsReads,
        };
    }

    /// <summary>Every event in ModelDb.AllEvents (event/option coverage roster).</summary>
    public Dictionary<string, object?> DumpEvents()
    {
        var events = new List<object?>();
        var skipped = 0;
        foreach (var e in ModelDb.AllEvents)
        {
            try
            {
                events.Add(new Dictionary<string, object?>
                {
                    ["id"] = e.Id.Entry,
                    ["name"] = _loc.Event(e.Id.Entry),
                    ["type"] = e.GetType().Name,
                    ["is_deterministic"] = e.IsDeterministic,
                });
            }
            catch (Exception ex)
            {
                skipped++;
                Log($"DumpEvents: skipping {e.Id.Entry}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return new Dictionary<string, object?>
        {
            ["type"] = "events", ["events"] = events, ["total"] = ModelDb.AllEvents.Count(), ["skipped"] = skipped,
        };
    }

    /// <summary>Every relic / potion / card with localized description and DynamicVars.</summary>
    public Dictionary<string, object?> DumpContent()
    {
        var relics = new List<object?>();
        foreach (var r in ModelDb.AllRelics)
        {
            try
            {
                var vars = new Dictionary<string, object?>();
                try { foreach (var dv in r.DynamicVars.Values) vars[dv.Name] = (int)dv.BaseValue; } catch { }
                relics.Add(new Dictionary<string, object?>
                {
                    ["id"] = r.Id.Entry, ["name"] = _loc.Relic(r.Id.Entry),
                    ["description"] = _loc.Bilingual("relics", r.Id.Entry + ".description"),
                    ["vars"] = vars.Count > 0 ? vars : null,
                });
            }
            catch { }
        }
        var potions = new List<object?>();
        foreach (var p in ModelDb.AllPotions)
        {
            try
            {
                var vars = new Dictionary<string, object?>();
                try { foreach (var dv in p.DynamicVars.Values) vars[dv.Name] = (int)dv.BaseValue; } catch { }
                potions.Add(new Dictionary<string, object?>
                {
                    ["id"] = p.Id.Entry, ["name"] = _loc.Potion(p.Id.Entry),
                    ["description"] = _loc.Bilingual("potions", p.Id.Entry + ".description"),
                    ["vars"] = vars.Count > 0 ? vars : null,
                });
            }
            catch { }
        }
        var cards = new List<object?>();
        foreach (var c in ModelDb.AllCards)
        {
            try
            {
                var cm = c.ToMutable();
                var stats = new Dictionary<string, object?>();
                try { foreach (var dv in cm.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }
                cards.Add(new Dictionary<string, object?>
                {
                    ["id"] = c.Id.Entry, ["name"] = _loc.Card(c.Id.Entry),
                    ["description"] = _loc.Bilingual("cards", c.Id.Entry + ".description"),
                    ["stats"] = stats.Count > 0 ? stats : null,
                    ["upgraded"] = cm.IsUpgraded,
                });
            }
            catch { }
        }
        return new Dictionary<string, object?>
        {
            ["type"] = "content", ["relics"] = relics, ["potions"] = potions, ["cards"] = cards,
        };
    }

    /// <summary>
    /// Oracle mode: after a non-final boss, enter the next act's starting Ancient node so the boss-relic
    /// 1-of-3 choice is surfaced as event_choice (spire-mentor DetectPostCombatState; the Rust engine hoists
    /// the same offer right after the boss card reward). Our HealBetweenActs already ran, so the Ancient's own
    /// between-act heal is a no-op.
    /// </summary>
    private void OracleEnterActStartAncient()
    {
        var nextMap = _runState?.Map;
        if (nextMap?.StartingMapPoint != null
            && nextMap.StartingMapPoint.PointType == MapPointType.Ancient
            && _runState!.CurrentRoom is MapRoom)
        {
            Log("Oracle mode: auto-entering act-start Ancient node for boss-relic choice");
            EnterRoomYielding(() => RunManager.Instance.EnterMapCoord(nextMap.StartingMapPoint.coord), "Act-start Ancient");
        }
    }
}
