using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoFisher.CommonCore.RawFileReader;

// Exploration probe: enumerate the FULL metadata surface a Thermo RAW exposes via IRawDataPlus,
// so we can see what exists beyond the mzML CV mapping. Metadata-only (a few scans) → fast even on
// a 22 GB file. Usage: rawmetaprobe <file.raw> [scanNumber]
class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: rawmetaprobe <file.raw> [scan]"); Environment.Exit(2); }
        var path = args[0];
        var raw = RawFileReaderFactory.ReadFile(path);
        if (raw == null || raw.IsError) { Console.Error.WriteLine("cannot open: " + path); Environment.Exit(2); }
        raw.SelectInstrument(Device.MS, 1);
        int first = raw.RunHeaderEx.FirstSpectrum, last = raw.RunHeaderEx.LastSpectrum;
        int scan = args.Length > 1 ? int.Parse(args[1]) : first;

        H("IRawDataPlus metadata API surface (public methods/properties)");
        foreach (var m in raw.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(x => !x.IsSpecialName && (x.Name.StartsWith("Get") || x.Name.Contains("Header") || x.Name.Contains("Tune")
                                 || x.Name.Contains("Status") || x.Name.Contains("Method") || x.Name.Contains("Trailer")
                                 || x.Name.Contains("Sample") || x.Name.Contains("Instrument") || x.Name.Contains("Error")))
                     .Select(x => x.Name).Distinct().OrderBy(x => x))
            Console.WriteLine("  " + m);

        H($"Run: {first}..{last}  ({raw.RunHeaderEx.SpectraCount} spectra)");

        DumpProps("InstrumentData (GetInstrumentData())", Safe(() => (object)raw.GetInstrumentData()));
        DumpProps("SampleInformation", Safe(() => (object)raw.SampleInformation));
        DumpProps("RunHeaderEx", Safe(() => (object)raw.RunHeaderEx));

        // Trailer Extra: the per-scan key/value bag. Header = the label set; values = this scan's row.
        H($"Trailer Extra labels + values  (scan {scan})");
        DumpLog(Safe(() => raw.GetTrailerExtraInformation(scan)));

        // Tune data (instrument tune snapshot, file-level, possibly several segments).
        H("Tune data (segment 0)");
        DumpLog(Safe(() => raw.GetTuneData(0)));

        // Status log nearest the run start (sensor/voltages timeseries per RT).
        H("Status log @ run start");
        DumpLog(Safe(() => raw.GetStatusLogForRetentionTime(raw.RunHeaderEx.StartTime)));

        // Instrument acquisition method(s) — free text, no CV equivalent.
        H("Instrument method(s)");
        try
        {
            int n = raw.InstrumentMethodsCount;
            Console.WriteLine($"  count={n}");
            for (int i = 0; i < n; i++)
            {
                var txt = raw.GetInstrumentMethod(i) ?? "";
                Console.WriteLine($"  method[{i}]: {txt.Length} chars; first line: {txt.Split('\n').FirstOrDefault()?.Trim()}");
            }
        }
        catch (Exception e) { Console.WriteLine("  (unavailable: " + e.Message + ")"); }

        H($"Trailer Extra TYPED values (scan {scan})");
        try
        {
            var vals = raw.GetTrailerExtraValues(scan);
            for (int i = 0; i < Math.Min(vals.Length, 8); i++)
                Console.WriteLine($"  [{i:00}] {vals[i]} :: {vals[i]?.GetType().Name ?? "null"}");
            Console.WriteLine($"  ...({vals.Length} typed values)");
        }
        catch (Exception e) { Console.WriteLine("  err: " + e.Message); }

        H("Status log API surface (signatures) + count");
        try { Console.WriteLine("  GetStatusLogEntriesCount() = " + raw.GetStatusLogEntriesCount()); } catch (Exception e) { Console.WriteLine("  count err: " + e.Message); }
        foreach (var m in raw.GetType().GetMethods().Where(x => x.Name.Contains("StatusLog")).Distinct())
            Console.WriteLine($"  {m.ReturnType.Name} {m.Name}(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
        H("IStatusLogEntry shape (props) + sample");
        try
        {
            var e0 = raw.GetStatusLogEntry(0);
            foreach (var p in e0.GetType().GetProperties()) Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
            var hdr = raw.GetStatusLogHeaderInformation();
            Console.WriteLine($"  header labels: {hdr.Length}; [0]={hdr[0].Label} :: {hdr[0].DataType}");
        }
        catch (Exception e) { Console.WriteLine("  err: " + e.Message); }

        H("Error log");
        try { int n = raw.RunHeaderEx.ErrorLogCount; Console.WriteLine($"  ErrorLogCount={n}"); for (int i = 0; i < Math.Min(n, 3); i++) { var el = raw.GetErrorLogItem(i); Console.WriteLine($"  [{i}] {el}"); } }
        catch (Exception e) { Console.WriteLine("  err: " + e.Message); }

        raw.Dispose();
    }

    static void H(string s) { Console.WriteLine("\n===== " + s + " ====="); }

    static T Safe<T>(Func<T> f) { try { return f(); } catch (Exception e) { Console.WriteLine("  (error: " + e.Message + ")"); return default; } }

    static void DumpProps(string title, object o)
    {
        H(title);
        if (o == null) { Console.WriteLine("  (null)"); return; }
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name))
        {
            object v; try { v = p.GetValue(o); } catch { continue; }
            Console.WriteLine($"  {p.Name} = {Format(v)}");
        }
    }

    static void DumpLog(ILogEntryAccess log)
    {
        if (log == null) { Console.WriteLine("  (null)"); return; }
        Console.WriteLine($"  ({log.Length} entries)");
        for (int i = 0; i < log.Length; i++)
            Console.WriteLine($"  [{i:00}] {log.Labels[i]} = {log.Values[i]}");
    }

    static string Format(object v)
    {
        if (v == null) return "(null)";
        if (v is System.Collections.IEnumerable e && !(v is string))
            return "[" + string.Join(", ", e.Cast<object>().Take(8).Select(x => x?.ToString())) + " ...]";
        return v.ToString();
    }
}
