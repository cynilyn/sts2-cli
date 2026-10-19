using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2Headless;

/// <summary>
/// dump_rng_trace: every named RNG stream the run holds, its initial and current state, and the first n draws
/// of a fixed call schedule. Read-only: draws run on clones (new Rng(ToSerializable())), never on the live
/// objects, so get_state / rng_counters are byte-identical before and after. Schema: logs/engine/p0/stage0d_oracle_cmds.md.
/// </summary>
public partial class RunSimulator
{
    public sealed record TraceCall(string Call, string Label, double[] Args);

    /// <summary>Default call schedule, cycled over the n draws.</summary>
    public static readonly TraceCall[] DefaultTraceSchedule =
    {
        new("NextInt", "NextInt(min,max)", new double[] { 0, 100 }),
        new("NextFloat", "NextFloat()", Array.Empty<double>()),
        new("NextInt", "NextInt(n)", new double[] { 100 }),
    };

    public static TraceCall? ParseTraceCall(string call, double[] args)
    {
        switch (call)
        {
            case "NextInt" when args.Length == 1: return new(call, "NextInt(n)", args);
            case "NextInt" when args.Length == 2: return new(call, "NextInt(min,max)", args);
            case "NextFloat" when args.Length == 0: return new(call, "NextFloat()", args);
            case "NextDouble" when args.Length == 0: return new(call, "NextDouble()", args);
            case "NextBool" when args.Length == 0: return new(call, "NextBool()", args);
            default: return null;
        }
    }

    private static Dictionary<string, object?> RngStateDict(SerializableRng s) => new()
    {
        ["state0"] = s.state0, ["state1"] = s.state1, ["state2"] = s.state2, ["state3"] = s.state3,
        ["counter"] = s.counter,
    };

    private static List<object?> TraceDraws(SerializableRng from, int n, TraceCall[] schedule)
    {
        var clone = new Rng(from);
        var list = new List<object?>(n);
        for (int i = 0; i < n; i++)
        {
            var c = schedule[i % schedule.Length];
            object value = c.Call switch
            {
                "NextInt" when c.Args.Length == 1 => clone.NextInt((int)c.Args[0]),
                "NextInt" => clone.NextInt((int)c.Args[0], (int)c.Args[1]),
                "NextFloat" => clone.NextFloat(),
                "NextDouble" => clone.NextDouble(),
                _ => clone.NextBool(),
            };
            list.Add(new Dictionary<string, object?>
            {
                ["call"] = c.Label, ["args"] = c.Args.Select(a => (object?)(long)a).ToList(), ["value"] = value,
            });
        }
        return list;
    }

    /// <summary>One stream entry. <paramref name="seed"/> = the Rng's construction seed; live = the game's object (null if derived only).</summary>
    private static Dictionary<string, object?> TraceStream(string scope, string formula, ulong seed, Rng? live, int n,
        TraceCall[] schedule)
    {
        var initial = new Rng(seed).ToSerializable();
        var d = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["seed_formula"] = formula,
            ["seed"] = seed,
            ["initial"] = RngStateDict(initial),
            ["draws"] = TraceDraws(initial, n, schedule),
        };
        if (live != null)
        {
            var cur = live.ToSerializable();
            d["current"] = RngStateDict(cur);
            d["next_draws"] = TraceDraws(cur, n, schedule);
        }
        else
        {
            d["current"] = null;
            d["next_draws"] = null;
        }
        return d;
    }

    private static Rng? ReflectRng(object? obj)
    {
        if (obj == null) return null;
        try { return GetInheritedField(obj, "_rng") as Rng; }
        catch { return null; }
    }

    public Dictionary<string, object?> DumpRngTrace(List<string>? only, int n, TraceCall[]? scheduleIn)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            if (n < 0 || n > 10000) return Error("n out of range");
            var schedule = scheduleIn is { Length: > 0 } ? scheduleIn : DefaultTraceSchedule;
            var rs = _runState.Rng;
            ulong runSeed = rs.Seed;
            var player = _runState.Players[0];
            var streams = new Dictionary<string, object?>();
            bool Want(string name) => only == null || only.Contains(name);
            void Add(string name, Dictionary<string, object?> e) { if (Want(name)) streams[name] = e; }

            // Run-level streams: Rng(runSeed, snake_case(RunRngType)).
            foreach (var t in Enum.GetValues<RunRngType>())
            {
                var name = StringHelper.SnakeCase(t.ToString());
                if (!Want(name)) continue;
                Add(name, TraceStream("run", "run_seed + hash(name)", runSeed + StringHelper.GetDeterministicHashCode(name),
                    rs.GetRng(t), n, schedule));
            }
            // Player-level streams: Rng(playerSeed, snake_case(PlayerRngType)); playerSeed = hash(seed) + slot.
            var prng = player.PlayerRng;
            foreach (var t in Enum.GetValues<PlayerRngType>())
            {
                var name = StringHelper.SnakeCase(t.ToString());
                if (!Want(name)) continue;
                Add(name, TraceStream("player", "player_seed + hash(name)", prng.Seed + StringHelper.GetDeterministicHashCode(name),
                    prng.GetRng(t), n, schedule));
            }
            // Derived streams (constructed on demand by the game, formula only unless a live object exists).
            ulong H(string s) => StringHelper.GetDeterministicHashCode(s);
            Add("act_selection", TraceStream("derived", "hash(seed_string) + hash(\"act_selection\")",
                H(rs.StringSeed) + H("act_selection"), null, n, schedule));
            for (int a = 1; a <= 3; a++)
            {
                var nm = $"act_{a}_map";
                Add(nm, TraceStream("derived", "run_seed + hash(\"act_N_map\")", runSeed + H(nm), null, n, schedule));
            }

            // Event models: Rng(runSeed + slot + hash(eventId)); the live object exists while the event is open.
            string actEventId = "";
            string encId = "";
            try
            {
                if (GetInheritedField(_runState.Act, "_rooms") is RoomSet rooms)
                {
                    actEventId = rooms.events.FirstOrDefault()?.Id.Entry ?? "";
                    encId = rooms.normalEncounters.FirstOrDefault()?.Id.Entry ?? "";
                }
            }
            catch { }
            Rng? liveEvent = null;
            string liveEventId = "";
            if (_runState.CurrentRoom is EventRoom er)
            {
                try { var ev = er.LocalMutableEvent; liveEvent = ev?.Rng; liveEventId = ev?.Id.Entry ?? ""; }
                catch { }
            }
            foreach (var eid in new[] { "NEOW", actEventId }.Where(s => s != "").Distinct())
                Add($"event:{eid}", TraceStream("derived", "run_seed + slot + hash(event_id)", runSeed + 0UL + H(eid),
                    eid == liveEventId ? liveEvent : null, n, schedule));
            if (encId != "")
            {
                ulong floor = (ulong)(_runState.TotalFloor + 1);
                Add($"encounter:{encId}", TraceStream("derived", "run_seed + total_floor + hash(encounter_id), total_floor = current+1",
                    runSeed + floor + H(encId), null, n, schedule));
            }
            // Live room objects (only while a combat room is open): the encounter's own Rng and each monster's Rng.
            if (_runState.CurrentRoom is CombatRoom cr)
            {
                try
                {
                    var enc = cr.Encounter;
                    var encRng = ReflectRng(enc);
                    if (enc != null && encRng != null)
                    {
                        var s = encRng.ToSerializable();
                        Add($"live_encounter:{enc.Id.Entry}", new Dictionary<string, object?>
                        {
                            ["scope"] = "live", ["seed_formula"] = "reflection of EncounterModel._rng", ["seed"] = null,
                            ["initial"] = null, ["draws"] = null, ["current"] = RngStateDict(s),
                            ["next_draws"] = TraceDraws(s, n, schedule),
                        });
                    }
                    var cs = CombatManager.Instance.DebugOnlyGetState();
                    int i = 0;
                    foreach (var e in cs?.Enemies ?? Enumerable.Empty<MegaCrit.Sts2.Core.Entities.Creatures.Creature>())
                    {
                        var mr = e?.Monster?.Rng;
                        if (mr != null)
                        {
                            var s = mr.ToSerializable();
                            Add($"live_monster:{i}:{e!.Monster!.Id.Entry}", new Dictionary<string, object?>
                            {
                                ["scope"] = "live", ["seed_formula"] = "monster.Rng", ["seed"] = null,
                                ["initial"] = null, ["draws"] = null, ["current"] = RngStateDict(s),
                                ["next_draws"] = TraceDraws(s, n, schedule),
                            });
                        }
                        i++;
                    }
                }
                catch { }
            }

            string[] actStreams = { "act_1_map", "act_2_map", "act_3_map" };
            var models = new List<object?>
            {
                Model("neow", new[] { "event:NEOW" }, "EventModel.Rng for the NEOW event (options + coin flips)"),
                Model("map", actStreams, "StandardActMap(Rng(seed,\"act_N_map\")); built when an act is entered, not held afterwards"),
                Model("encounter", new[] { "up_front", encId != "" ? $"encounter:{encId}" : "encounter:<id>" },
                    "room lists come from up_front (RoomSet generation); monster composition from EncounterModel._rng"),
                Model("card_reward", new[] { "rewards" }, "CardFactory.CreateForReward draws player.PlayerRng.Rewards (shop cards use shops)"),
                Model("relic_reward", new[] { "rewards", "up_front" }, "RelicFactory.RollRarity draws rewards; grab-bag fill/shuffle uses up_front at run start"),
                Model("potion_reward", new[] { "rewards" }, "PotionReward draws player.PlayerRng.Rewards"),
                Model("shop", new[] { "shops" }, "MerchantInventory draws player.PlayerRng.Shops (+ rewards for relic rarity)"),
                Model("event", actEventId != "" ? new[] { "unknown_map_point", $"event:{actEventId}" } : new[] { "unknown_map_point" },
                    "'?' room type from unknown_map_point; event content from the per-event EventModel.Rng"),
                Model("combat_shuffle", new[] { "shuffle" }, "RunRngSet.Shuffle: draw-pile shuffles and random-position inserts"),
                Model("monster_hp", new[] { "niche" }, "Creature.SetUniqueMonsterHpValue(RunState.Rng.Niche); monster.Rng (live_monster:*) drives monster AI picks"),
            };

            return new Dictionary<string, object?>
            {
                ["type"] = "rng_trace",
                ["seed_string"] = rs.StringSeed,
                ["run_seed"] = runSeed,
                ["player_seed"] = prng.Seed,
                ["n"] = n,
                ["schedule"] = schedule.Select(c => (object?)new Dictionary<string, object?>
                    { ["call"] = c.Label, ["args"] = c.Args.Select(a => (object?)(long)a).ToList() }).ToList(),
                ["streams"] = streams,
                ["models"] = models,
            };
        }
        catch (Exception ex) { return ErrorWithTrace("DumpRngTrace failed", ex); }

        static Dictionary<string, object?> Model(string model, string[] streams, string note) => new()
        {
            ["model"] = model, ["streams"] = streams.ToList(), ["note"] = note,
        };
    }
}
