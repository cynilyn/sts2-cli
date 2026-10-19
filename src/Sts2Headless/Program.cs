using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Headless;

class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Locate the directory containing sts2.dll: STS2_LIB env, walk up from BaseDirectory, then BaseDirectory/lib.
    /// </summary>
    private static string ResolveLibDirectory()
    {
        var envLib = Environment.GetEnvironmentVariable("STS2_LIB");
        if (!string.IsNullOrWhiteSpace(envLib))
        {
            var p = Path.GetFullPath(envLib.Trim());
            if (Directory.Exists(p) && File.Exists(Path.Combine(p, "sts2.dll")))
                return p;
        }

        var dir = AppContext.BaseDirectory;
        for (var depth = 0; depth < 16 && !string.IsNullOrEmpty(dir); depth++)
        {
            var candidate = Path.Combine(dir, "lib");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "sts2.dll")))
                return Path.GetFullPath(candidate);
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "lib"));
    }

    /// <summary>
    /// Windows wakes sleeping threads on its scheduler tick, 64Hz by default, so Sleep(1), Sleep(5) and
    /// Sleep(10) all really take ~15.6ms and the engine's iteration-count wait loops run up to ~15x longer
    /// than intended. timeBeginPeriod(1) raises the tick to 1kHz for this process; since Windows 10 2004
    /// the request is per-process, so the engine must ask for itself (the Python host asking does not help).
    /// Ported from Zaaacqwq/sts2-cli fa56e7d.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            try { TimeBeginPeriod(1); } catch { /* timer precision is an optimisation, not a requirement */ }
        }

        // Prevent unhandled exceptions from crashing the process
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"[FATAL] Unhandled: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"[WARN] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        var libDir = ResolveLibDirectory();

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var path = Path.Combine(libDir, name.Name + ".dll");
            if (File.Exists(path))
                return ctx.LoadFromAssemblyPath(Path.GetFullPath(path));

            // Also check game directory (via STS2_GAME_DIR env var)
            var gameDir = Environment.GetEnvironmentVariable("STS2_GAME_DIR") ?? "";
            if (!string.IsNullOrEmpty(gameDir))
            {
                path = Path.Combine(gameDir, name.Name + ".dll");
                if (File.Exists(path))
                    return ctx.LoadFromAssemblyPath(path);
            }

            return null;
        };

        if (RunSimulator.OracleMode) OracleStartup(libDir);

        var sim = new RunSimulator();
        WriteLine(new Dictionary<string, object?> { ["type"] = "ready", ["version"] = "0.2.0" });
        var oracleHang = RunSimulator.OracleMode && Environment.GetEnvironmentVariable("STS2_ORACLE_TEST_HANG") == "1";
        var commandCount = 0;

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            // Interop preflight only: simulate a wedged oracle on the second command.
            if (oracleHang && ++commandCount >= 2) Thread.Sleep(Timeout.Infinite);

            Dictionary<string, object?>? result;
            try
            {
                var cmd = JsonSerializer.Deserialize<JsonElement>(line);
                result = HandleCommand(sim, cmd);
            }
            catch (JsonException ex)
            {
                result = new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Invalid JSON: {ex.Message}" };
            }
            catch (Exception ex)
            {
                result = new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"{ex.GetType().Name}: {ex.Message}", ["stack_trace"] = ex.ToString() };
            }

            if (result != null)
            {
                WriteLine(result);
                if (result.TryGetValue("type", out var resultTypeObj) &&
                    string.Equals(resultTypeObj as string, "quit_result", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
    }

    static Dictionary<string, object?>? HandleCommand(RunSimulator sim, JsonElement cmd)
    {
        var cmdType = cmd.GetProperty("cmd").GetString() ?? "";
        switch (cmdType)
        {
            case "start_run":
                return sim.StartRun(
                    cmd.TryGetProperty("character", out var ch) ? ch.GetString() ?? "Ironclad" : "Ironclad",
                    cmd.TryGetProperty("ascension", out var asc) ? asc.GetInt32() : 0,
                    cmd.TryGetProperty("seed", out var s) ? s.GetString() : null,
                    cmd.TryGetProperty("lang", out var lang) ? lang.GetString() ?? "en" : "en",
                    // Parity-oracle extras (spire-mentor harness); absent = previous behaviour.
                    cmd.TryGetProperty("played_save", out var ps) && ps.ValueKind == JsonValueKind.True,
                    cmd.TryGetProperty("act1", out var a1) && a1.ValueKind == JsonValueKind.String ? a1.GetString() : null
                );

            case "roll_acts":
                return sim.RollActs(
                    cmd.TryGetProperty("seed", out var rs) ? rs.GetString() : null,
                    cmd.TryGetProperty("played_save", out var rps) && rps.ValueKind == JsonValueKind.True
                );

            case "action":
            {
                var action = cmd.GetProperty("action").GetString() ?? "";
                Dictionary<string, object?>? actionArgs = null;
                if (cmd.TryGetProperty("args", out var argsElem))
                {
                    actionArgs = new Dictionary<string, object?>();
                    foreach (var prop in argsElem.EnumerateObject())
                    {
                        actionArgs[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetInt32(),
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.ToString(),
                        };
                    }
                }
                return sim.ExecuteAction(action, actionArgs);
            }

            case "load_save":
            {
                var savePath = cmd.TryGetProperty("path", out var sp) ? sp.GetString() : null;
                var saveJson = cmd.TryGetProperty("json", out var sj) ? sj.GetString() : null;
                if (saveJson == null && savePath != null)
                {
                    if (!File.Exists(savePath))
                        return new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Save file not found: {savePath}" };
                    saveJson = File.ReadAllText(savePath);
                }
                if (saveJson == null)
                    return new Dictionary<string, object?> { ["type"] = "error", ["message"] = "Provide 'path' or 'json' for load_save" };
                var loadLang = cmd.TryGetProperty("lang", out var le) ? (le.GetString() ?? "en") : "en";
                return sim.LoadSave(saveJson, loadLang);
            }
            case "get_map":
                return sim.GetFullMap();

            case "set_player":
            {
                var args = new Dictionary<string, JsonElement>();
                foreach (var prop in cmd.EnumerateObject())
                    if (prop.Name != "cmd") args[prop.Name] = prop.Value;
                return sim.SetPlayer(args);
            }

            case "enter_room":
            {
                var roomType = cmd.TryGetProperty("type", out var rt) ? rt.GetString() ?? "" : "";
                var encounter = cmd.TryGetProperty("encounter", out var enc) ? enc.GetString() : null;
                var eventId = cmd.TryGetProperty("event", out var ev) ? ev.GetString() : null;
                return sim.EnterRoom(roomType, encounter, eventId);
            }

            case "start_combat":
            {
                // Atomic episode reset for RL: new seeded run -> optional player setup -> encounter -> optional
                // draw order. Fails closed: the first error is returned unchanged.
                var r = sim.StartRun(
                    cmd.TryGetProperty("character", out var sch) ? sch.GetString() ?? "Ironclad" : "Ironclad",
                    cmd.TryGetProperty("ascension", out var sasc) ? sasc.GetInt32() : 0,
                    cmd.TryGetProperty("seed", out var sseed) ? sseed.GetString() : null,
                    cmd.TryGetProperty("lang", out var slang) ? slang.GetString() ?? "en" : "en");
                if (r != null && Equals(r.GetValueOrDefault("type"), "error")) return r;
                if (cmd.TryGetProperty("player", out var spl) && spl.ValueKind == JsonValueKind.Object)
                {
                    var pargs = new Dictionary<string, JsonElement>();
                    foreach (var prop in spl.EnumerateObject()) pargs[prop.Name] = prop.Value;
                    var pr = sim.SetPlayer(pargs);
                    if (pr != null && Equals(pr.GetValueOrDefault("type"), "error")) return pr;
                }
                var encounter = cmd.TryGetProperty("encounter", out var senc) ? senc.GetString() : null;
                if (string.IsNullOrEmpty(encounter))
                    return new Dictionary<string, object?> { ["type"] = "error", ["message"] = "start_combat requires 'encounter'" };
                var er = sim.EnterRoom("combat", encounter, null);
                if (er != null && Equals(er.GetValueOrDefault("type"), "error")) return er;
                if (cmd.TryGetProperty("draw_order", out var sdo) && sdo.ValueKind == JsonValueKind.Array)
                {
                    var order = new List<string>();
                    foreach (var c in sdo.EnumerateArray()) order.Add(c.GetString() ?? "");
                    var dr = sim.SetDrawOrder(order);
                    if (dr != null && Equals(dr.GetValueOrDefault("type"), "error")) return dr;
                }
                return er;
            }

            case "get_draw_pile":
                return sim.GetDrawPile();

            // Parity-oracle commands (spire-mentor sts2-pycli harness). See RunSimulator.Oracle.cs.
            case "legal_actions":
                return sim.LegalActions();

            case "get_state":
                return sim.GetState();

            case "dump_rng_trace":
            {
                List<string>? only = null;
                if (cmd.TryGetProperty("streams", out var st) && st.ValueKind == JsonValueKind.Array)
                    only = st.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                int n = cmd.TryGetProperty("n", out var nn) ? nn.GetInt32() : 10;
                RunSimulator.TraceCall[]? sched = null;
                if (cmd.TryGetProperty("schedule", out var sc) && sc.ValueKind == JsonValueKind.Array)
                {
                    var l = new List<RunSimulator.TraceCall>();
                    foreach (var it in sc.EnumerateArray())
                    {
                        var args = it.TryGetProperty("args", out var aa) ? aa.EnumerateArray().Select(x => x.GetDouble()).ToArray() : Array.Empty<double>();
                        var tc = RunSimulator.ParseTraceCall(it.GetProperty("call").GetString() ?? "", args);
                        if (tc == null) return new Dictionary<string, object?> { ["type"] = "error", ["message"] = "bad schedule entry" };
                        l.Add(tc);
                    }
                    sched = l.ToArray();
                }
                return sim.DumpRngTrace(only, n, sched);
            }

            case "dump_rooms":
                return sim.DumpRooms();

            case "dump_maps":
                return sim.DumpMaps();

            case "dump_card_reward":
                return sim.DumpCardReward(
                    cmd.TryGetProperty("count", out var dcc) ? dcc.GetInt32() : 3,
                    cmd.TryGetProperty("room_type", out var drt) ? (drt.GetString() ?? "Monster") : "Monster");

            case "dump_relic_reward":
                return sim.DumpRelicReward(cmd.TryGetProperty("count", out var drc) ? drc.GetInt32() : 1);

            case "dump_shop":
                return sim.DumpShop();

            case "dump_content":
                return sim.DumpContent();

            case "dump_events":
                return sim.DumpEvents();

            case "set_draw_order":
            {
                var cards = new List<string>();
                if (cmd.TryGetProperty("cards", out var cardsArr))
                    foreach (var c in cardsArr.EnumerateArray())
                        cards.Add(c.GetString() ?? "");
                return sim.SetDrawOrder(cards);
            }

            case "write_continue_save":
            {
                var outputPath = cmd.TryGetProperty("path", out var op) ? op.GetString() : null;
                return sim.SaveCheckpoint(outputPath);
            }

            case "quit":
            {
                var outputPath = cmd.TryGetProperty("path", out var op) ? op.GetString() : null;
                if (!string.IsNullOrEmpty(outputPath))
                {
                    var saveResult = sim.SaveCheckpoint(outputPath);
                    bool saveOk = saveResult.TryGetValue("success", out var sObj) && sObj is bool b && b;
                    if (!saveOk)
                    {
                        // Save failed — do NOT clean up so the caller can retry with a different path.
                        return new Dictionary<string, object?>
                        {
                            ["type"] = "save_error",
                            ["save"] = saveResult,
                        };
                    }
                    sim.CleanUp();
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "quit_result",
                        ["success"] = true,
                        ["save"] = saveResult,
                    };
                }
                sim.CleanUp();
                return new Dictionary<string, object?>
                {
                    ["type"] = "quit_result",
                    ["success"] = true,
                    ["save"] = null,
                };
            }

            default:
                return new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Unknown command: {cmdType}" };
        }
    }

    /// <summary>
    /// STS2_ORACLE=1 startup (parity-oracle launch manifest): UTF-8 stderr (the harness drains it as UTF-8),
    /// optional stderr copy to STS2_ORACLE_STDERR_DIR/oracle_&lt;pid&gt;.log, optional self affinity
    /// (STS2_ORACLE_AFFINITY, hex mask) + BelowNormal, and one "[ORACLE] {json}" stderr line with pid, lib
    /// directory, DLL hashes and runtime. Nothing here touches stdout.
    /// </summary>
    static void OracleStartup(string libDir)
    {
        var utf8 = new System.Text.UTF8Encoding(false);
        TextWriter err = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
        var dir = Environment.GetEnvironmentVariable("STS2_ORACLE_STDERR_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
                var file = new StreamWriter(Path.Combine(dir, $"oracle_{Environment.ProcessId}.log"), false, utf8) { AutoFlush = true };
                err = new TeeWriter(err, file);
            }
            catch (Exception ex) { err.WriteLine($"[ORACLE] stderr copy failed: {ex.Message}"); }
        }
        Console.SetError(TextWriter.Synchronized(err));

        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var aff = Environment.GetEnvironmentVariable("STS2_ORACLE_AFFINITY");
        string? affErr = null;
        if (!string.IsNullOrEmpty(aff))
        {
            try
            {
                proc.ProcessorAffinity = (IntPtr)Convert.ToInt64(aff.Replace("0x", ""), 16);
                proc.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) { affErr = ex.Message; }
        }
        proc.Refresh();

        static string? Sha(string p)
        {
            try { using var f = File.OpenRead(p); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f)); }
            catch { return null; }
        }
        var baseDir = AppContext.BaseDirectory;
        var manifest = new Dictionary<string, object?>
        {
            ["pid"] = Environment.ProcessId,
            ["start"] = DateTimeOffset.Now.ToString("o"),
            ["exe_dir"] = baseDir,
            ["lib_dir"] = libDir,
            ["sts2_dll_sha256"] = Sha(Path.Combine(libDir, "sts2.dll")),
            ["headless_dll_sha256"] = Sha(Path.Combine(baseDir, "Sts2Headless.dll")),
            ["godot_stub_sha256"] = Sha(Path.Combine(baseDir, "GodotSharp.dll")),
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["affinity"] = $"0x{(long)proc.ProcessorAffinity:X}",
            ["priority"] = proc.PriorityClass.ToString(),
            ["affinity_error"] = affErr,
            ["env"] = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(e => ((string)e.Key).StartsWith("STS2_"))
                .ToDictionary(e => (string)e.Key, e => (object?)e.Value),
        };
        Console.Error.WriteLine("[ORACLE] " + JsonSerializer.Serialize(manifest));
    }

    sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _a, _b;
        public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
        public override System.Text.Encoding Encoding => _a.Encoding;
        public override void Write(char value) { _a.Write(value); _b.Write(value); }
        public override void Write(string? value) { _a.Write(value); _b.Write(value); }
        public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
        public override void Flush() { _a.Flush(); _b.Flush(); }
    }

    static void WriteLine(Dictionary<string, object?> data)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(data, JsonOpts));
        Console.Out.Flush();
    }
}
