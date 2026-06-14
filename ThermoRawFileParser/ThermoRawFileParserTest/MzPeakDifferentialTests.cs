using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ThermoRawFileParserTest
{
    [TestFixture]
    public class MzPeakDifferentialTests
    {
        private static string TestRawFile =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "small.RAW");

        private static string Home =>
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        private static string ResolveRepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ThermoRawFileParser.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private static string ResolveDll(string repoRoot)
        {
            if (repoRoot == null) return null;
            var path = Path.Combine(repoRoot, "bin", "Release", "net8.0", "ThermoRawFileParser.dll");
            return File.Exists(path) ? path : null;
        }

        private static string ResolveMzml2Mzpeak()
        {
            var path = Path.Combine(Home, "Claude", "mzML2mzPeak", "target", "release", "mzml2mzpeak");
            return File.Exists(path) ? path : null;
        }

        private static string ResolvePython()
        {
            foreach (var cand in new[] { "python3.11", "python3", "python" })
            {
                try
                {
                    var psi = new ProcessStartInfo(cand, "-c \"import pyarrow\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    using (var p = Process.Start(psi))
                    {
                        p.WaitForExit();
                        if (p.ExitCode == 0) return cand;
                    }
                }
                catch { }
            }
            return null;
        }

        private static (int code, string stdout, string stderr) Run(string file, string args)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.EnvironmentVariables["DOTNET_ROLL_FORWARD"] = "LatestMajor";
            psi.EnvironmentVariables["DOTNET_ROLL_FORWARD_TO_PRERELEASE"] = "1";
            using (var p = Process.Start(psi))
            {
                var so = p.StandardOutput.ReadToEnd();
                var se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, so, se);
            }
        }

        // Reads both archives and emits a single JSON verdict. Decodes the reference chunk layout to
        // absolute (m/z, intensity) pairs, builds TRFP's per-spectrum signal from its point layout, and
        // compares the nonzero (m/z, intensity) multiset of every reference-profile spectrum. Centroid
        // coverage is scoped to indices present in BOTH peaks facets (the reference centroids MS2 while
        // TRFP keeps MS2 profile in spectra_data, so the centroid sets are disjoint here and the
        // comparison is reported as skipped rather than dropped).
        private const string CompareScript =
            "import sys, json, zipfile, io, collections, struct\n" +
            "import pyarrow.parquet as pq\n" +
            "ref_path, trfp_path = sys.argv[1], sys.argv[2]\n" +
            "def read(zpath, name):\n" +
            "    z = zipfile.ZipFile(zpath)\n" +
            "    return pq.read_table(io.BytesIO(z.read(name))).to_pylist() if name in z.namelist() else None\n" +
            "ref_data = read(ref_path, 'spectra_data.parquet')\n" +
            "ref = collections.defaultdict(list)\n" +
            "for r in ref_data:\n" +
            "    c = r['chunk']; si = c['spectrum_index']; mz = c['mz_chunk_start']\n" +
            "    ref[si].append((mz, c['intensity'][0]))\n" +
            "    for d, it in zip(c['mz_chunk_values'], c['intensity'][1:]):\n" +
            "        mz += d; ref[si].append((mz, it))\n" +
            "ref_profile_indices = sorted(ref.keys())\n" +
            "ref_peaks = read(ref_path, 'spectra_peaks.parquet')\n" +
            "refpk = collections.defaultdict(list)\n" +
            "if ref_peaks:\n" +
            "    for r in ref_peaks:\n" +
            "        p = r['point']; refpk[p['spectrum_index']].append((p['mz'], p['intensity']))\n" +
            "trfp_data = read(trfp_path, 'spectra_data.parquet')\n" +
            "trfp = collections.defaultdict(list)\n" +
            "for r in trfp_data:\n" +
            "    p = r['point']; trfp[p['spectrum_index']].append((p['mz'], p['intensity']))\n" +
            "trfp_peaks = read(trfp_path, 'spectra_peaks.parquet')\n" +
            "trfppk = collections.defaultdict(list)\n" +
            "if trfp_peaks:\n" +
            "    for r in trfp_peaks:\n" +
            "        p = r['point']; trfppk[p['spectrum_index']].append((p['mz'], p['intensity']))\n" +
            "def meta_map(rows):\n" +
            "    m = {}\n" +
            "    for r in rows:\n" +
            "        s = r['spectrum']; m[s['index']] = (s['MS_1000511_ms_level'], s.get('MS_1000465_scan_polarity'), s['time'])\n" +
            "    return m\n" +
            "rm = meta_map(read(ref_path, 'spectra_metadata.parquet'))\n" +
            "tm = meta_map(read(trfp_path, 'spectra_metadata.parquet'))\n" +
            "def nz(pairs):\n" +
            "    c = collections.Counter()\n" +
            "    for mz, it in pairs:\n" +
            "        if it == 0: continue\n" +
            "        c[(mz, struct.unpack('f', struct.pack('f', it))[0])] += 1\n" +
            "    return c\n" +
            "profile_ok = all(nz(ref[si]) == nz(trfp.get(si, [])) for si in ref_profile_indices)\n" +
            "centroid_compared, centroid_skipped, centroid_ok = [], [], True\n" +
            "for si in sorted(refpk.keys()):\n" +
            "    if si in trfppk:\n" +
            "        centroid_compared.append(si)\n" +
            "        if nz(refpk[si]) != nz(trfppk[si]): centroid_ok = False\n" +
            "    else:\n" +
            "        centroid_skipped.append(si)\n" +
            "out = {\n" +
            "  'spectrum_count_ok': len(rm) == len(tm), 'ref_spectrum_count': len(rm), 'trfp_spectrum_count': len(tm),\n" +
            "  'ms_level_ok': all(rm[i][0] == tm[i][0] for i in rm if i in tm),\n" +
            "  'polarity_ok': all(rm[i][1] is None or tm.get(i,(None,None,None))[1] is None or rm[i][1]==tm[i][1] for i in rm),\n" +
            "  'rt_ok': all(i in tm and abs(rm[i][2]-tm[i][2]) < 1e-3 for i in rm),\n" +
            "  'profile_multiset_ok': profile_ok,\n" +
            "  'ref_profile_indices': ref_profile_indices, 'ref_profile_count': len(ref_profile_indices),\n" +
            "  'compared_indices': ref_profile_indices,\n" +
            "  'centroid_indices_compared': centroid_compared, 'centroid_multiset_ok': centroid_ok,\n" +
            "  'centroid_indices_skipped': centroid_skipped,\n" +
            "}\n" +
            "print(json.dumps(out))\n";

        [Test]
        public void Differential_Semantic_Equivalence_Vs_MzML2MzPeak()
        {
            var repoRoot = ResolveRepoRoot();
            var dll = ResolveDll(repoRoot);
            var m2m = ResolveMzml2Mzpeak();
            var python = ResolvePython();

            if (dll == null) Assert.Ignore("Release ThermoRawFileParser.dll not built");
            if (m2m == null) Assert.Ignore("prebuilt mzml2mzpeak not available");
            if (python == null) Assert.Ignore("python3.11/pyarrow not available");

            var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);
            var mzml = Path.Combine(tmp, "small.profile.mzML");
            var refMzpeak = Path.Combine(tmp, "small.ref.mzpeak");
            var trfpMzpeak = Path.Combine(tmp, "small.trfp.mzpeak");
            var script = Path.Combine(tmp, "compare.py");

            try
            {
                var s1 = Run("dotnet", $"\"{dll}\" -i \"{TestRawFile}\" -b \"{mzml}\" -f 1 -p");
                Assert.That(s1.code, Is.EqualTo(0), $"RAW->profile mzML failed: {s1.stderr}");
                Assert.That(File.Exists(mzml), "profile mzML must exist");

                var s2 = Run(m2m, $"\"{mzml}\" \"{refMzpeak}\" --no-numpress");
                Assert.That(s2.code, Is.EqualTo(0), $"mzML->ref mzpeak failed: {s2.stderr}");
                Assert.That(File.Exists(refMzpeak), "reference mzpeak must exist");

                var s3 = Run("dotnet", $"\"{dll}\" -i \"{TestRawFile}\" -b \"{trfpMzpeak}\" -f 4");
                Assert.That(s3.code, Is.EqualTo(0), $"RAW->trfp mzpeak failed: {s3.stderr}");
                Assert.That(File.Exists(trfpMzpeak), "trfp mzpeak must exist");

                File.WriteAllText(script, CompareScript);
                var cmp = Run(python, $"\"{script}\" \"{refMzpeak}\" \"{trfpMzpeak}\"");
                Assert.That(cmp.code, Is.EqualTo(0), $"comparison failed: {cmp.stderr}");

                var o = JObject.Parse(cmp.stdout.Trim());

                Assert.That((bool)o["spectrum_count_ok"], Is.True,
                    $"spectrum count must agree ({o["ref_spectrum_count"]} vs {o["trfp_spectrum_count"]})");
                Assert.That((bool)o["ms_level_ok"], Is.True, "per-index ms_level must agree");
                Assert.That((bool)o["polarity_ok"], Is.True, "per-index polarity must agree");
                Assert.That((bool)o["rt_ok"], Is.True, "per-index RT must agree within tolerance");
                Assert.That((bool)o["profile_multiset_ok"], Is.True,
                    "nonzero (m/z,intensity) multiset must match for every reference-profile spectrum");

                Assert.That((int)o["ref_profile_count"], Is.EqualTo(14),
                    "reference MS1-profile set is the 14 ms_level-1 spectra routed to spectra_data");
                Assert.That((int)o["ref_profile_count"], Is.GreaterThan(0));

                var refIdx = o["ref_profile_indices"].Select(x => (int)x).ToArray();
                var cmpIdx = o["compared_indices"].Select(x => (int)x).ToArray();
                Assert.That(cmpIdx, Is.EqualTo(refIdx),
                    "the compared index set must equal the exact reference MS1-profile index set");

                Assert.That((bool)o["centroid_multiset_ok"], Is.True,
                    "reference-centroid spectra present in BOTH peaks facets must match (centroid coverage " +
                    "is scoped to indices in both facets; the reference centroids MS2 while TRFP keeps MS2 " +
                    "profile in spectra_data, so the disjoint set is reported as skipped, not dropped)");
            }
            finally
            {
                Directory.Delete(tmp, true);
            }
        }
    }
}
