using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Parquet.Schema;

namespace ThermoRawFileParserTest
{
    // Shared helpers for the mzPeak test suites: archive entry extraction, schema-leaf lookup,
    // python/pyarrow/validator discovery, and a single pyarrow launch+JSON helper. Centralizes the
    // copies that previously lived in each MzPeak*Tests file.
    internal static class MzPeakTestSupport
    {
        // Reads a named entry from an mzPeak (zip) archive, or null when absent.
        public static byte[] ReadEntry(string archive, string name)
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                var entry = zip.GetEntry(name);
                if (entry == null) return null;
                using (var s = entry.Open())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        // Resolves a parquet leaf DataField by its dotted/slashed path.
        public static DataField Leaf(ParquetSchema schema, string path)
        {
            return schema.GetDataFields().First(d => d.Path.ToString() == path);
        }

        // First python interpreter on PATH that can import pyarrow, or null.
        public static string ResolvePython()
        {
            foreach (var cand in new[] { "python3", "python" })
            {
                try
                {
                    var psi = new ProcessStartInfo(cand, "-c \"import pyarrow\"")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    using (var p = Process.Start(psi)) { p.WaitForExit(); if (p.ExitCode == 0) return cand; }
                }
                catch { }
            }
            return null;
        }

        // The validator command if present on PATH, else null.
        public static string ResolveValidator()
        {
            try
            {
                var psi = new ProcessStartInfo("mzpeak-validate", "--help")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using (var p = Process.Start(psi)) { p.WaitForExit(); return "mzpeak-validate"; }
            }
            catch { return null; }
        }

        // Writes the named archive entry to a temp parquet file, runs the pyarrow snippet ({PARQUET}
        // token = temp path), and returns the snippet's stdout. Ignores when python/pyarrow is absent.
        public static string PyArrowRaw(string archive, string entry, string snippet)
        {
            var python = ResolvePython();
            if (python == null) Assert.Ignore("python3/pyarrow not available");

            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".parquet");
            File.WriteAllBytes(path, ReadEntry(archive, entry));
            try
            {
                var code = snippet.Replace("{PARQUET}", path.Replace("\\", "\\\\"));
                var psi = new ProcessStartInfo(python, "-c \"" + code.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using (var proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    Assert.That(proc.ExitCode, Is.EqualTo(0), $"pyarrow failed: {stderr}");
                    return stdout.Trim();
                }
            }
            finally { File.Delete(path); }
        }

        // PyArrowRaw whose snippet prints a single JSON object.
        public static JObject PyArrow(string archive, string entry, string snippet) =>
            JObject.Parse(PyArrowRaw(archive, entry, snippet));

        // PyArrowRaw whose snippet prints a JSON array of objects.
        public static System.Collections.Generic.List<JObject> PyArrowArray(string archive, string entry, string snippet) =>
            JArray.Parse(PyArrowRaw(archive, entry, snippet)).Cast<JObject>().ToList();

        // The null-aware delta-decode python function used by the differential snippets: cumulative
        // add for present deltas, absolute restart after a null, nulls preserved. Prepend to a snippet
        // and call dd(start, values).
        public const string DdFunc =
            "def dd(start, arr):\n" +
            "    buf=[]; last=start\n" +
            "    if not arr: return [start]\n" +
            "    if arr[0] is None:\n" +
            "        if len(arr)>1 and arr[1] is None: buf.append(last)\n" +
            "        last=None\n" +
            "    else: buf.append(start)\n" +
            "    for it in arr:\n" +
            "        if it is not None:\n" +
            "            if last is not None: last=it+last; buf.append(last)\n" +
            "            else: buf.append(it); last=it\n" +
            "        else: buf.append(None); last=None\n" +
            "    return buf\n";
    }
}
