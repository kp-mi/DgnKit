using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Bentley.Interop.MicroStationDGN;

// MicroStation COM oracle: dumps ground truth for DgnKit tests.
// usage: MstnOracle.exe [--linestyles] <outDir> <file.dgn | @listfile> [...]
//   default       <name>.oracle.json      models/levels/elements via COM
//   --linestyles  <name>.linestyles.json  line style definitions (components, strokes) via MicroStation's in-process
//                                         Python DgnPlatform API (tools/oracle/linestyle_oracle.py, run by key-in)
//   --gcs-only    <name>.gcs.oracle.json  model headers + geographic coordinate systems (ModelReference.GetGCS) only
// The oracle only drives a MicroStation process it launched itself: if COM hands back an already-running instance
// (e.g. the user's interactive MicroStation/ORD), it aborts without opening files or calling Quit().
internal static class Program
{
    private static readonly string[] MstnProcessNames = { "ustation", "MicroStation", "OpenRoadsDesigner", "OpenBridgeModeler", "OpenRailDesigner", "OpenSiteDesigner", "OpenUtilitiesDesigner" };

    private static HashSet<int> MstnProcessIds() =>
        new HashSet<int>(MstnProcessNames.SelectMany(n => { try { return Process.GetProcessesByName(n); } catch { return new Process[0]; } }).Select(p => p.Id));

    private static int Main(string[] args)
    {
        bool lineStyles = args.Length > 0 && args[0] == "--linestyles";
        if (lineStyles) args = args.Skip(1).ToArray();
        bool gcsOnly = args.Length > 0 && args[0] == "--gcs-only";
        if (gcsOnly) args = args.Skip(1).ToArray();
        if (args.Length < 2) { Console.WriteLine("usage: MstnOracle [--linestyles|--gcs-only] <outDir> <file.dgn|@list.txt>..."); return 1; }
        string outDir = Directory.CreateDirectory(args[0]).FullName;
        var files = new List<string>();
        foreach (var a in args.Skip(1))
        {
            if (a.StartsWith("@")) files.AddRange(File.ReadAllLines(a.Substring(1)).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")));
            else files.Add(a);
        }
        Application app = null;
        Process interactive = null; // the MicroStation process StartInteractive launched (waited for after Quit)
        Application Start()
        {
            Console.WriteLine("Starting MicroStation automation...");
            var before = MstnProcessIds();
            var a = (Application)Activator.CreateInstance(Type.GetTypeFromProgID("MicroStationDGN.Application"));
            int pid = -1;
            try { pid = a.ProcessID; } catch { }
            if (pid <= 0 || before.Contains(pid))
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(a); } catch { }
                throw new OracleAbort($"COM returned a pre-existing MicroStation process (pid {pid}); refusing to drive it. Run the oracle when no other MicroStation/ORD session is open.");
            }
            Console.WriteLine($"MicroStation {a.Version} (pid {pid})");
            return a;
        }
        // Line styles need queued key-ins ("python load"), which a COM-launched (-Embedding) instance never processes:
        // start MicroStation as a normal interactive process on the first file and attach to exactly that pid.
        Application StartInteractive(string firstFile)
        {
            // tolerate a previous instance that is still shutting down (wait, never touch it)
            var wait = Stopwatch.StartNew();
            while (MstnProcessIds().Count > 0 && wait.Elapsed < TimeSpan.FromSeconds(60)) System.Threading.Thread.Sleep(1000);
            var others = MstnProcessIds();
            if (others.Count > 0)
                throw new OracleAbort($"another MicroStation/ORD process is running (pid {string.Join(", ", others)}); run the line-style oracle when none is open.");
            string exe = Path.Combine(Environment.GetEnvironmentVariable("MSTN_DIR") ?? @"C:\Program Files\Bentley\MicroStation 2026\MicroStation", "microstation.exe");
            Console.WriteLine($"Starting interactive MicroStation on {Path.GetFileName(firstFile)}...");
            var psi = new ProcessStartInfo(exe, "\"" + firstFile + "\"") { UseShellExecute = false };
            // this instance only: no modal open alerts (e.g. "No WorkSet Alert" blocks startup and COM registration)
            // and no WorkSet association written into the opened files
            psi.EnvironmentVariables["MS_NO_OPEN_ALERTS"] = "1";
            psi.EnvironmentVariables["MS_SUPPRESS_FILE_WORKSET_ASSOCIATION"] = "1";
            var proc = Process.Start(psi);
            interactive = proc;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromMinutes(4) && !proc.HasExited)
            {
                System.Threading.Thread.Sleep(2000);
                object o = null;
                try { o = System.Runtime.InteropServices.Marshal.GetActiveObject("MicroStationDGN.Application"); } catch { continue; }
                var a = (Application)o;
                int pid = -1;
                try { pid = a.ProcessID; } catch { }
                if (pid == proc.Id)
                {
                    // wait until the design file is open (startup dialogs/workspace load finished)
                    if (Try(() => a.ActiveDesignFile != null)) { Console.WriteLine($"MicroStation {a.Version} (pid {pid}, interactive)"); return a; }
                    continue;
                }
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(a); } catch { }
                if (pid > 0) { Try(() => proc.CloseMainWindow()); throw new OracleAbort($"COM returned MicroStation pid {pid}, not the one started ({proc.Id})."); }
            }
            string windows = proc.HasExited ? "(exited)" : WindowsOf(proc.Id);
            if (!proc.HasExited) Try(() => proc.CloseMainWindow()); // graceful close request; never killed
            throw new OracleAbort($"interactive MicroStation (pid {proc.Id}) did not become available over COM within 4 minutes; asked it to close. Windows: {windows}");
        }
        foreach (var f in files)
        {
            string path = Path.GetFullPath(f);
            string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + (lineStyles ? ".linestyles.json" : gcsOnly ? ".gcs.oracle.json" : ".oracle.json"));
            // Dimension crash guard: some dimensions crash the automation server inside Drop()/text accessors. The id
            // being dumped is written to <out>.guard; after a crash the retry skips that dimension's detail dump.
            DimGuardPath = outPath + ".guard";
            SkipDims.Clear();
            try { File.Delete(DimGuardPath); } catch { }
            // a line-style script failure is deterministic: report it and go on with the same instance
            int maxAttempts = lineStyles ? 1 : 2;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    app ??= lineStyles ? StartInteractive(path) : Start();
                    if (lineStyles) DumpLineStyles(app, path, outPath);
                    else Dump(app, path, outPath, gcsOnly);
                    Console.WriteLine($"   wrote {outPath} ({sw.ElapsedMilliseconds} ms)");
                    break;
                }
                catch (OracleAbort ex) { Console.WriteLine("ABORT: " + ex.Message); QuitOwn(app, interactive); return 3; }
                catch (Exception ex)
                {
                    Console.WriteLine($"   FAILED (attempt {attempt}) {path}: {ex.Message}");
                    if (lineStyles && Try(() => app.ProcessID, -1) == interactive?.Id) continue; // instance still answers: keep it
                    string crashed = Try(() => File.Exists(DimGuardPath) ? File.ReadAllText(DimGuardPath).Trim() : null);
                    if (!string.IsNullOrEmpty(crashed) && SkipDims.Add(crashed) && maxAttempts < 12)
                    {
                        Console.WriteLine($"   dimension {crashed} was being dumped: skipping its details on retry");
                        maxAttempts++;
                    }
                    try { File.Delete(DimGuardPath); } catch { }
                    // The automation server may have died (RPC unavailable): restart it for the next attempt/file.
                    // an interactive instance takes a while to shut down; never restart while our own one is still exiting
                    if (!QuitOwn(app, interactive))
                    { Console.WriteLine($"ABORT: MicroStation pid {interactive?.Id} did not exit after Quit()"); return 3; }
                    app = null;
                    interactive = null;
                    System.Threading.Thread.Sleep(3000);
                }
            }
        }
        if (!QuitOwn(app, interactive)) Console.WriteLine($"WARNING: MicroStation pid {interactive?.Id} did not exit after Quit()");
        return 0;
    }

    private static string J(object v)
    {
        switch (v)
        {
            case null: return "null";
            case bool b: return b ? "true" : "false";
            case double d: return double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture);
            case float fl: return ((double)fl).ToString("R", CultureInfo.InvariantCulture);
            case int or long or short or byte or uint or ulong: return Convert.ToString(v, CultureInfo.InvariantCulture);
            case Enum en: return Convert.ToInt64(en).ToString(CultureInfo.InvariantCulture);
        }
        var s = v.ToString();
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static string P(Point3d p) => "[" + J(p.X) + "," + J(p.Y) + "," + J(p.Z) + "]";

    private static T Try<T>(Func<T> f, T def = default) { try { return f(); } catch { return def; } }

    /// <summary>
    /// Asks our own MicroStation to quit (COM Quit, retried: it can reject calls right after a failure) and, for an
    /// interactive instance we started, waits for its process to exit. Never kills. False if it is still running.
    /// </summary>
    private static bool QuitOwn(Application app, Process own)
    {
        if (app == null) return own == null || WaitGone(own.Id, TimeSpan.FromSeconds(30));
        for (int i = 0; i < 5; i++)
        {
            try { app.Quit(); } catch { }
            if (own == null) return true;
            if (WaitGone(own.Id, TimeSpan.FromSeconds(20))) return true;
        }
        return false;
    }

    /// <summary>Waits until no process with this id exists (polling; never kills it).</summary>
    private static bool WaitGone(int pid, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            bool alive;
            try { using (var p = Process.GetProcessById(pid)) alive = !p.HasExited; }
            catch (ArgumentException) { alive = false; } // no such process
            catch { alive = true; }
            if (!alive) return true;
            System.Threading.Thread.Sleep(500);
        }
        return false;
    }

    private sealed class OracleAbort : Exception { public OracleAbort(string m) : base(m) { } }

    /// <summary>Primary/reference GCS of a model (null when none), plus master→lon/lat for sample points.</summary>
    private static void AppendGcs(StringBuilder sb, ModelReference m, string key, bool primary, IList<Point3d> samples)
    {
        GeographicCoordinateSystem g = Try(() => m.GetGCS(primary));
        sb.Append(",\"").Append(key).Append("\":");
        if (g == null || string.IsNullOrEmpty(Try(() => g.Name))) { sb.Append("null"); return; }
        sb.Append("{\"name\":").Append(J(Try(() => g.Name))).Append(",\"description\":").Append(J(Try(() => g.Description)))
          .Append(",\"source\":").Append(J(Try(() => g.Source))).Append(",\"projection\":").Append(J(Try(() => g.ProjectionName)))
          .Append(",\"datum\":").Append(J(Try(() => g.DatumName))).Append(",\"datumDescription\":").Append(J(Try(() => g.DatumDescription)))
          .Append(",\"ellipsoid\":").Append(J(Try(() => g.EllipsoidName))).Append(",\"ellipsoidDescription\":").Append(J(Try(() => g.EllipsoidDescription)))
          .Append(",\"verticalDatum\":").Append(J(Try(() => g.VerticalDatumName))).Append(",\"units\":").Append(J(Try(() => g.Units)))
          .Append(",\"falseEasting\":").Append(J(Try(() => g.FalseEasting))).Append(",\"falseNorthing\":").Append(J(Try(() => g.FalseNorthing)))
          .Append(",\"scaleReduction\":").Append(J(Try(() => g.ScaleReduction)))
          .Append(",\"equatorialRadius\":").Append(J(Try(() => g.EllipsoidEquatorialRadius))).Append(",\"polarRadius\":").Append(J(Try(() => g.EllipsoidPolarRadius)))
          .Append(",\"minLon\":").Append(J(Try(() => g.MinimumLongitude))).Append(",\"maxLon\":").Append(J(Try(() => g.MaximumLongitude)))
          .Append(",\"minLat\":").Append(J(Try(() => g.MinimumLatitude))).Append(",\"maxLat\":").Append(J(Try(() => g.MaximumLatitude)));
        sb.Append(",\"samples\":[");
        bool fs = true;
        foreach (var p in samples)
        {
            var q = p;
            var ll = Try(() => g.LatLongFromMasterUnits(ref q), new GeoPoint3D { Longitude = double.NaN, Latitude = double.NaN });
            if (!fs) sb.Append(','); fs = false;
            sb.Append("{\"master\":").Append(P(p)).Append(",\"lon\":").Append(J(ll.Longitude)).Append(",\"lat\":").Append(J(ll.Latitude)).Append('}');
        }
        sb.Append("]}");
    }

    /// <summary>Up to <paramref name="n"/> sample points (range low corners of the first graphic elements) in master units.</summary>
    private static List<Point3d> SamplePoints(ModelReference m, int n)
    {
        var pts = new List<Point3d>();
        try
        {
            var ee = m.Scan(new ElementScanCriteriaClass());
            while (pts.Count < n && ee.MoveNext())
            {
                var e = ee.Current;
                if (!e.IsGraphical) continue;
                var r = Try(() => e.Range);
                if (double.IsNaN(r.Low.X) || (r.Low.X == 0 && r.Low.Y == 0)) continue;
                pts.Add(r.Low);
            }
        }
        catch { }
        return pts;
    }

    private static void Dump(Application app, string path, string outPath, bool gcsOnly)
    {
        Console.WriteLine("== " + path);
        app.OpenDesignFile(path, true, MsdV7Action.Workmode);
        var df = app.ActiveDesignFile;
        var sb = new StringBuilder();
        sb.Append("{\"generator\":\"MicroStation COM oracle (typed)\",\"msVersion\":").Append(J(app.Version)).Append(",\"file\":").Append(J(path));
        sb.Append(",\"format\":").Append(J((int)df.Format));
        bool first;
        if (gcsOnly)
        {
            sb.Append(",\"models\":[");
            first = true;
            foreach (ModelReference m in df.Models)
            {
                if (!first) sb.Append(','); first = false;
                Try(() => { m.Activate(); return 0; });
                sb.Append("{\"name\":").Append(J(m.Name)).Append(",\"type\":").Append(J((int)m.Type)).Append(",\"is3d\":").Append(J(m.Is3D))
                  .Append(",\"masterUnit\":").Append(J(Try(() => m.get_MasterUnit().Label))).Append(",\"uorPerMaster\":").Append(J(m.UORsPerMasterUnit));
                var samples = SamplePoints(m, 5);
                AppendGcs(sb, m, "gcs", true, samples);
                AppendGcs(sb, m, "referenceGcs", false, samples);
                sb.Append('}');
            }
            sb.Append("]}");
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            try { df.Close(); } catch { }
            return;
        }

        sb.Append(",\"levels\":[");
        first = true;
        foreach (Level l in df.Levels)
        {
            if (!first) sb.Append(','); first = false;
            sb.Append("{\"id\":").Append(J(l.ID)).Append(",\"number\":").Append(J(Try(() => l.Number))).Append(",\"code\":").Append(J(Try(() => l.Number)))
              .Append(",\"name\":").Append(J(l.Name)).Append(",\"description\":").Append(J(Try(() => l.Description)))
              .Append(",\"color\":").Append(J(Try(() => l.ElementColor))).Append(",\"weight\":").Append(J(Try(() => l.ElementLineWeight)))
              .Append(",\"style\":").Append(J(Try(() => l.ElementLineStyle?.Name))).Append(",\"styleNumber\":").Append(J(Try(() => (int?)l.ElementLineStyle?.Number)))
              .Append(",\"displayed\":").Append(J(Try(() => l.IsDisplayed))).Append(",\"frozen\":").Append(J(Try(() => l.IsFrozen))).Append(",\"plot\":").Append(J(Try(() => l.Plot)))
              .Append(",\"locked\":").Append(J(Try(() => l.IsLocked))).Append(",\"overrideColor\":").Append(J(Try(() => l.OverrideColor)))
              .Append(",\"overrideWeight\":").Append(J(Try(() => l.OverrideLineWeight))).Append('}');
        }
        sb.Append("],\"lineStyles\":[");
        first = true;
        foreach (LineStyle s in df.LineStyles)
        {
            if (!first) sb.Append(','); first = false;
            sb.Append("{\"name\":").Append(J(s.Name)).Append(",\"number\":").Append(J(Try(() => (int?)s.Number))).Append('}');
        }
        sb.Append("],\"models\":[");
        first = true;
        foreach (ModelReference m in df.Models)
        {
            if (!first) sb.Append(','); first = false;
            Try(() => { m.Activate(); return 0; });
            sb.Append("{\"name\":").Append(J(m.Name)).Append(",\"description\":").Append(J(Try(() => m.Description))).Append(",\"type\":").Append(J((int)m.Type))
              .Append(",\"is3d\":").Append(J(m.Is3D)).Append(",\"uorPerMaster\":").Append(J(m.UORsPerMasterUnit)).Append(",\"uorPerStorage\":").Append(J(Try(() => m.UORsPerStorageUnit)))
              .Append(",\"masterUnit\":").Append(J(Try(() => m.get_MasterUnit().Label))).Append(",\"subUnit\":").Append(J(Try(() => m.get_SubUnit().Label)))
              .Append(",\"globalOrigin\":").Append(P(Try(() => m.GlobalOrigin)));
            AppendGcs(sb, m, "gcs", true, SamplePoints(m, 5));
            sb.Append(",\"elements\":[");
            var ee = m.Scan(new ElementScanCriteriaClass());
            int n = 0; bool fe = true;
            while (ee.MoveNext())
            {
                var e = ee.Current;
                n++;
                if (!fe) sb.Append(','); fe = false;
                sb.Append("{\"id\":").Append(J(Try(() => e.ID64.ToString(CultureInfo.InvariantCulture))))
                  .Append(",\"type\":").Append(J((int)e.Type)).Append(",\"level\":").Append(J(Try(() => e.Level?.Name)))
                  .Append(",\"color\":").Append(J(Try(() => e.Color))).Append(",\"weight\":").Append(J(Try(() => e.LineWeight)))
                  .Append(",\"style\":").Append(J(Try(() => e.LineStyle?.Name))).Append(",\"class\":").Append(J(Try(() => (int)e.Class)))
                  .Append(",\"graphic\":").Append(J(e.IsGraphical));
                if (e.IsGraphical)
                {
                    var r = Try(() => e.Range);
                    sb.Append(",\"range\":[").Append(P(r.Low)).Append(',').Append(P(r.High)).Append(']');
                }
                try { AppendGeometry(sb, e); } catch { }
                sb.Append('}');
            }
            sb.Append("],\"elementCount\":").Append(n).Append('}');
        }
        sb.Append("]}");
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        try { df.Close(); } catch { }
    }

    /// <summary>
    /// COM's LineStyle only has Name/Number, so the definitions come from MicroStation's own DgnPlatform line style API
    /// (LineStyleHandle → LsComponent tree) through its embedded Python: a wrapper script with the output path baked in
    /// is queued as a `python load` key-in against the opened file, and we wait for the JSON it writes.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);

    /// <summary>8.3 form of an existing path (unchanged when short names are unavailable).</summary>
    private static string ShortPath(string path)
    {
        var sb = new StringBuilder(1024);
        uint n = GetShortPathName(path, sb, (uint)sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : path;
    }

    private static void DumpLineStyles(Application app, string path, string outPath)
    {
        Console.WriteLine("== " + path + " (line styles)");
        string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "linestyle_oracle.py");
        if (!File.Exists(script))
            script = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\linestyle_oracle.py"));
        if (!File.Exists(script)) throw new FileNotFoundException("linestyle_oracle.py not found", script);
        // Stage everything in %TEMP%; the key-in gets the wrapper's 8.3 path (key-in arguments split at spaces).
        string work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dgnkit-ls-oracle")).FullName;
        string tmpOut = Path.Combine(work, "out.json");
        string wrapper = Path.Combine(work, "run_ls_oracle.py");
        string started = tmpOut + ".started", err = tmpOut + ".err";
        foreach (var p in new[] { tmpOut, started, err }) if (File.Exists(p)) File.Delete(p);
        // The marker is written before any MicroStation import, so a missing marker means the key-in never ran Python.
        File.WriteAllText(wrapper,
            "OUT_PATH = r\"" + tmpOut + "\"\nopen(OUT_PATH + '.started', 'w').write('1')\ntry:\n    exec(compile(open(r\"" + script +
            "\", encoding='utf-8').read(), 'linestyle_oracle.py', 'exec'), {'__name__': '__main__', 'OUT_PATH': OUT_PATH})\n" +
            "except BaseException:\n    import traceback\n    open(OUT_PATH + '.err', 'w').write(traceback.format_exc())\n",
            new UTF8Encoding(false));

        // the interactive instance was started on the first file: re-opening it could raise a dialog that blocks key-ins
        var df = Try(() => app.ActiveDesignFile);
        if (df == null || !string.Equals(Path.GetFullPath(Try(() => df.FullName) ?? ""), path, StringComparison.OrdinalIgnoreCase))
        {
            app.OpenDesignFile(path, true, MsdV7Action.Workmode);
            df = app.ActiveDesignFile;
        }
        try
        {
            // Queued key-ins run in MicroStation's UI loop (interactive instance, see StartInteractive). A COM-launched
            // -Embedding instance never processes them, and GetInput-pumping it blocks (tried).
            int pid = Try(() => app.ProcessID, -1);
            if (!KeyinQueueRuns(app)) Console.WriteLine("   note: a test key-in (active color) was not processed within 30 s; windows: " + WindowsOf(pid));
            // The key-in splits its argument at spaces (e.g. "C:\Users\First Last\..."): 8.3 folder, but the real file
            // name (the short name "RUN_LS~1.PY" is rejected: the Python app wants a lower-case .py/.pyc extension).
            string arg = Path.Combine(ShortPath(Path.GetDirectoryName(wrapper)), Path.GetFileName(wrapper));
            string keyin = "python load " + (arg.Contains(" ") ? "\"" + arg + "\"" : arg);
            Console.WriteLine("   key-in: " + keyin);
            app.CadInputQueue.SendKeyin(keyin);
            var sw = Stopwatch.StartNew();
            while (!File.Exists(tmpOut) && !File.Exists(err) && sw.Elapsed < TimeSpan.FromSeconds(150)) System.Threading.Thread.Sleep(250);
            if (File.Exists(err)) throw new InvalidOperationException("line style script failed: " + File.ReadAllText(err));
            if (!File.Exists(tmpOut))
                throw new TimeoutException((File.Exists(started) ? "line style script started but wrote no output"
                    : "the 'python load' key-in never ran") + "; windows: " + WindowsOf(pid));
            File.Copy(tmpOut, outPath, true);
        }
        finally { try { df.Close(); } catch { } }
    }

    /// <summary>Whether MicroStation processes queued key-ins: toggles the active colour and waits for it to change.</summary>
    private static bool KeyinQueueRuns(Application app)
    {
        int before = Try(() => app.ActiveSettings.Color, -1);
        int target = before == 3 ? 4 : 3;
        app.CadInputQueue.SendKeyin("active color " + target);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (Try(() => app.ActiveSettings.Color, -1) == target) { Console.WriteLine($"   key-in queue ok ({sw.ElapsedMilliseconds} ms)"); return true; }
            System.Threading.Thread.Sleep(250);
        }
        return false;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);

    /// <summary>Visible top-level windows of a process ("title [class]"), to spot modal dialogs blocking the UI loop.</summary>
    private static string WindowsOf(int pid)
    {
        if (pid <= 0) return "?";
        var list = new List<string>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p == pid && IsWindowVisible(h))
            {
                var t = new StringBuilder(256); var c = new StringBuilder(128);
                GetWindowText(h, t, t.Capacity); GetClassName(h, c, c.Capacity);
                list.Add($"'{t}' [{c}]");
            }
            return list.Count < 20;
        }, IntPtr.Zero);
        return list.Count == 0 ? "(none visible)" : string.Join("; ", list);
    }

    private static void AppendGeometry(StringBuilder sb, Element e)
    {
        switch ((int)e.Type)
        {
            case 17:
            {
                var t = e.AsTextElement();
                sb.Append(",\"text\":").Append(J(t.Text)).Append(",\"origin\":").Append(P(t.get_Origin()))
                  .Append(",\"height\":").Append(J(Try(() => t.TextStyle.Height))).Append(",\"width\":").Append(J(Try(() => t.TextStyle.Width)))
                  .Append(",\"font\":").Append(J(Try(() => t.TextStyle.Font.Name)));
                break;
            }
            case 7:
            {
                var tn = e.AsTextNodeElement();
                var lines = new List<string>();
                for (int i = 1; i <= Try(() => tn.TextLinesCount); i++) lines.Add(Try(() => tn.get_TextLine(i)));
                sb.Append(",\"lines\":[").Append(string.Join(",", lines.Select(J))).Append("],\"origin\":").Append(P(tn.Origin));
                break;
            }
            case 2:
            {
                var c = e.AsCellElement();
                sb.Append(",\"name\":").Append(J(c.Name)).Append(",\"origin\":").Append(P(c.Origin));
                break;
            }
            case 35:
            {
                var c = e.AsSharedCellElement();
                sb.Append(",\"name\":").Append(J(c.Name)).Append(",\"origin\":").Append(P(c.Origin));
                break;
            }
            case 3:
            case 4:
            {
                var v = e.AsLineElement().GetVertices();
                sb.Append(",\"points\":[").Append(string.Join(",", v.Select(P))).Append(']');
                break;
            }
            case 6:
            {
                var v = e.AsShapeElement().GetVertices();
                sb.Append(",\"points\":[").Append(string.Join(",", v.Select(P))).Append(']');
                break;
            }
            case 15:
            {
                var a = e.AsEllipseElement();
                sb.Append(",\"center\":").Append(P(a.CenterPoint)).Append(",\"primary\":").Append(J(a.PrimaryRadius)).Append(",\"secondary\":").Append(J(a.SecondaryRadius));
                break;
            }
            case 16:
            {
                var a = e.AsArcElement();
                sb.Append(",\"center\":").Append(P(a.CenterPoint)).Append(",\"primary\":").Append(J(a.PrimaryRadius)).Append(",\"secondary\":").Append(J(a.SecondaryRadius))
                  .Append(",\"start\":").Append(J(a.StartAngle)).Append(",\"sweep\":").Append(J(a.SweepAngle)).Append(",\"startPoint\":").Append(P(a.StartPoint));
                break;
            }
            case 33:
            {
                var dsb = new StringBuilder();
                AppendDimension(dsb, e);
                sb.Append(dsb);
                break;
            }
            case 37:
            {
                var t = e.AsTagElement();
                sb.Append(",\"tagValue\":").Append(J(Try(() => t.Value))).Append(",\"tagName\":").Append(J(Try(() => t.TagDefinitionName)))
                  .Append(",\"tagSet\":").Append(J(Try(() => t.TagSetName))).Append(",\"tagTarget\":").Append(J(Try(() => t.BaseElement.ID64.ToString(CultureInfo.InvariantCulture))))
                  .Append(",\"origin\":").Append(P(t.Origin)).Append(",\"hidden\":").Append(J(Try(() => t.IsHidden)));
                break;
            }
        }
    }

    private static string M(Matrix3d m) => "[" + P(m.RowX) + "," + P(m.RowY) + "," + P(m.RowZ) + "]";

    private static string N(Func<object> f) { try { return J(f()); } catch { return "null"; } }

    /// <summary>
    /// Dimension (33): type, definition points, per-segment texts/values, style values and MicroStation's regenerated
    /// graphics (Element.Drop — returned elements are not added to the model).
    /// Index bases of Point/segment accessors are probed: "pointsRaw"/"segmentsRaw" hold index 0..count (null = threw).
    /// </summary>
    private static string DimGuardPath;
    private static readonly HashSet<string> SkipDims = new HashSet<string>();

    private static void AppendDimension(StringBuilder sb, Element e)
    {
        string id = Try(() => e.ID64.ToString(CultureInfo.InvariantCulture), "");
        if (SkipDims.Contains(id)) { sb.Append(",\"dimSkipped\":true"); return; }
        try { File.WriteAllText(DimGuardPath, id); } catch { }
        AppendDimensionCore(sb, e);
        try { File.Delete(DimGuardPath); } catch { }
    }

    private static void AppendDimensionCore(StringBuilder sb, Element e)
    {
        var d = e.AsDimensionElement();
        int np = Try(() => d.ReferencePointsCount, 0);
        int ns = Try(() => d.SegmentsCount, 0);
        sb.Append(",\"dimType\":").Append(N(() => (int)d.DimensionType))
          .Append(",\"dimPointCount\":").Append(J(np)).Append(",\"dimSegmentCount\":").Append(J(ns))
          .Append(",\"dimHeight\":").Append(N(() => d.DimHeight))
          .Append(",\"dimRotation\":").Append(Try(() => M(d.get_Rotation()), "null"))
          .Append(",\"dimAnnotationScale\":").Append(N(() => d.ModelAnnotationScale));
        sb.Append(",\"pointsRaw\":[");
        for (int i = 0; i <= np; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Try(() => P(d.get_Point(i)), "null"));
        }
        sb.Append("],\"segmentsRaw\":[");
        for (int i = 0; i <= ns; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"primary\":").Append(N(() => d.get_PrimaryText(i, MsdDimTextField.Main)))
              .Append(",\"secondary\":").Append(N(() => d.get_SecondaryText(i, MsdDimTextField.Main)))
              .Append(",\"hasSecondary\":").Append(N(() => d.get_HasSecondaryText(i)))
              .Append(",\"value\":").Append(N(() => d.get_ActualValue(i)))
              .Append(",\"just\":").Append(N(() => (int)d.get_TextJustificationPerSegment(i)))
              .Append(",\"textRotation\":").Append(N(() => d.get_TextRotationPerSegment(i)))
              .Append('}');
        }
        sb.Append(']');
        var st = Try(() => d.DimensionStyle);
        if (st != null)
        {
            sb.Append(",\"dimStyle\":{\"name\":").Append(N(() => st.Name))
              .Append(",\"textHeight\":").Append(N(() => st.TextHeight)).Append(",\"textWidth\":").Append(N(() => st.TextWidth))
              .Append(",\"termWidth\":").Append(N(() => st.TerminatorWidth)).Append(",\"termHeight\":").Append(N(() => st.TerminatorHeight))
              .Append(",\"extOffset\":").Append(N(() => st.ExtensionLineOffset)).Append(",\"extExtend\":").Append(N(() => st.ExtensionLineExtend))
              .Append(",\"leftMargin\":").Append(N(() => st.LeftMargin)).Append(",\"lowerMargin\":").Append(N(() => st.LowerMargin))
              .Append(",\"accuracy\":").Append(N(() => (int)st.PrimaryAccuracy)).Append(",\"textJust\":").Append(N(() => (int)st.TextJustification))
              .Append(",\"textLocation\":").Append(N(() => (int)st.TextLocation)).Append(",\"alignment\":").Append(N(() => (int)st.Alignment))
              .Append(",\"arrowhead\":").Append(N(() => (int)st.TerminatorArrowhead)).Append(",\"termMode\":").Append(N(() => (int)st.TerminatorMode))
              .Append(",\"showExt\":").Append(N(() => st.DisplayExtensionLines)).Append(",\"annScale\":").Append(N(() => st.AnnotationScale))
              .Append(",\"font\":").Append(N(() => st.OverallFont.Name)).Append(",\"primaryFormat\":").Append(N(() => (int)st.PrimaryFormat))
              .Append(",\"masterLabel\":").Append(N(() => st.PrimaryMasterUnitLabel)).Append(",\"subLabel\":").Append(N(() => st.PrimarySubUnitLabel))
              .Append('}');
        }
        // Regenerated graphics
        ElementEnumerator dropped = Try(() => d.Drop()) ?? Try(() => e.AsDroppableElement().Drop());
        if (dropped == null) return;
        var dsb = new StringBuilder();
        bool first = true;
        int count = 0;
        try
        {
            while (count < 400 && dropped.MoveNext()) AppendDropped(dsb, dropped.Current, ref first, ref count, 0);
        }
        catch { }
        sb.Append(",\"dropped\":[").Append(dsb).Append(']');
    }

    private static void AppendDropped(StringBuilder sb, Element c, ref bool first, ref int count, int depth)
    {
        count++;
        if (!first) sb.Append(','); first = false;
        int t = Try(() => (int)c.Type, -1);
        sb.Append("{\"type\":").Append(J(t)).Append(",\"color\":").Append(N(() => c.Color)).Append(",\"weight\":").Append(N(() => c.LineWeight))
          .Append(",\"style\":").Append(N(() => c.LineStyle?.Name));
        // Each case builds its fragment separately so a COM failure never leaves half-written JSON.
        string extra = "";
        try
        {
            switch (t)
            {
                case 17:
                {
                    var x = c.AsTextElement();
                    extra = ",\"text\":" + N(() => x.Text) + ",\"origin\":" + Try(() => P(x.get_Origin()), "null")
                      + ",\"height\":" + N(() => x.TextStyle.Height) + ",\"width\":" + N(() => x.TextStyle.Width)
                      + ",\"rot\":" + Try(() => M(x.get_Rotation()), "null") + ",\"font\":" + N(() => x.TextStyle.Font.Name)
                      + ",\"just\":" + N(() => (int)x.TextStyle.Justification);
                    break;
                }
                case 7:
                {
                    var x = c.AsTextNodeElement();
                    var lines = new List<string>();
                    for (int i = 1; i <= Try(() => x.TextLinesCount); i++) lines.Add(Try(() => x.get_TextLine(i)));
                    extra = ",\"lines\":[" + string.Join(",", lines.Select(J)) + "],\"origin\":" + Try(() => P(x.Origin), "null");
                    break;
                }
                case 3: case 4:
                    extra = ",\"points\":[" + string.Join(",", c.AsLineElement().GetVertices().Select(P)) + "]";
                    break;
                case 6:
                    extra = ",\"points\":[" + string.Join(",", c.AsShapeElement().GetVertices().Select(P)) + "]";
                    break;
                case 15:
                {
                    var a = c.AsEllipseElement();
                    extra = ",\"center\":" + P(a.CenterPoint) + ",\"primary\":" + J(a.PrimaryRadius) + ",\"secondary\":" + J(a.SecondaryRadius);
                    break;
                }
                case 16:
                {
                    var a = c.AsArcElement();
                    extra = ",\"center\":" + P(a.CenterPoint) + ",\"primary\":" + J(a.PrimaryRadius)
                      + ",\"start\":" + J(a.StartAngle) + ",\"sweep\":" + J(a.SweepAngle);
                    break;
                }
                case 2: case 12: case 14:
                {
                    var csb = new StringBuilder();
                    bool f2 = true;
                    var sub = c.AsComplexElement().GetSubElements();
                    while (count < 400 && sub.MoveNext()) AppendDropped(csb, sub.Current, ref f2, ref count, depth + 1);
                    extra = ",\"children\":[" + csb + "]";
                    break;
                }
            }
        }
        catch { }
        sb.Append(extra).Append('}');
    }
}
