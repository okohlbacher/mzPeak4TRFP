#!/usr/bin/env python3.11
"""End-to-end corpus test: for every Thermo RAW that has a reference mzPeak in the
mzML2mzPeak corpus, convert RAW -> mzPeak with TRFP, validate it, and compare it to the
reference. Resumable, smallest-first, bounded concurrency, per-file timeout, temp cleanup.

  python3.11 tools/e2e/run_corpus_e2e.py [--workers N] [--timeout SEC] [--max-gb G] [--limit K]

Writes tools/e2e/out/results.json (resume state) + a stamped report.md. Re-running skips
pairs already recorded. Exit 0 if all PASS, 1 if any non-PASS, 2 on harness error.
"""
import os, sys, json, time, subprocess, tempfile, argparse, shutil
from concurrent.futures import ThreadPoolExecutor, as_completed

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
PAIRS = os.path.join(HERE, "corpus_pairs.json")
OUTDIR = os.path.join(HERE, "out")
RESULTS = os.path.join(OUTDIR, "results.json")
DLL = os.path.join(REPO, "ThermoRawFileParser/bin/x64/Release/net8.0/ThermoRawFileParser.dll")
X64 = os.path.expanduser("~/.dotnet-x64/dotnet")
ENV = {**os.environ, "DOTNET_ROLL_FORWARD": "LatestMajor", "DOTNET_ROLL_FORWARD_TO_PRERELEASE": "1"}

def convert(raw, out, timeout):
    cmd = ["arch", "-x86_64", X64, DLL, "-i", raw, "-b", out, "-f", "mzpeak"]
    t = time.time()
    p = subprocess.run(cmd, env=ENV, capture_output=True, text=True, timeout=timeout)
    return p.returncode == 0 and os.path.exists(out), round(time.time() - t, 1), p.stdout + p.stderr

def validate(mzp, timeout):
    try:
        p = subprocess.run(["mzpeak-validate", mzp], capture_output=True, text=True, timeout=timeout)
        line = next((l for l in p.stdout.splitlines() if "validation:" in l), p.stdout[:120])
        return ("PASS" in line), line.strip()
    except Exception as e:
        return False, f"validator-error: {e}"

def compare(ours, ref, timeout):
    try:
        p = subprocess.run(["python3.11", os.path.join(HERE, "compare_mzpeak.py"), ours, ref],
                           capture_output=True, text=True, timeout=timeout)
        return json.loads(p.stdout.strip().splitlines()[-1])
    except Exception as e:
        return {"verdict": "ERROR", "error": f"{type(e).__name__}: {e}"}

def run_one(pair, timeout):
    raw, ref = pair["raw"], pair["ref_mzpeak"]
    name = os.path.basename(raw)
    rec = {"raw": raw, "ref": ref, "name": name, "raw_mb": pair.get("raw_mb"), "match": pair.get("match")}
    tmpd = tempfile.mkdtemp(prefix="e2e_")
    out = os.path.join(tmpd, "ours.mzpeak")
    try:
        ok, secs, log = convert(raw, out, timeout)
        rec["convert_secs"] = secs
        if not ok:
            rec.update(status="CONVERT_FAIL", detail=log[-400:]); return rec
        vok, vline = validate(out, max(120, timeout // 4))
        rec["validate"] = vline
        cmp = compare(out, ref, max(300, timeout // 2))
        rec["compare"] = cmp
        if cmp.get("verdict") == "PASS" and vok:
            rec["status"] = "PASS"
        elif cmp.get("verdict") == "ERROR":
            rec["status"] = "COMPARE_ERROR"
        elif not vok:
            rec["status"] = "VALIDATE_FAIL"
        else:
            rec["status"] = "DIFF"
        return rec
    except subprocess.TimeoutExpired:
        rec.update(status="TIMEOUT", detail=f">{timeout}s"); return rec
    except Exception as e:
        rec.update(status="ERROR", detail=f"{type(e).__name__}: {e}"); return rec
    finally:
        shutil.rmtree(tmpd, ignore_errors=True)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--workers", type=int, default=3)
    ap.add_argument("--timeout", type=int, default=1800)
    ap.add_argument("--max-gb", type=float, default=None, help="skip RAW larger than this many GB")
    ap.add_argument("--limit", type=int, default=None, help="only run the first K (smallest) pairs")
    a = ap.parse_args()
    os.makedirs(OUTDIR, exist_ok=True)
    if not os.path.exists(DLL):
        print("ERROR: x64 Release DLL not built:", DLL); sys.exit(2)
    pairs = json.load(open(PAIRS))
    pairs.sort(key=lambda p: p.get("raw_mb") or 0)            # smallest first
    if a.max_gb is not None:
        pairs = [p for p in pairs if (p.get("raw_mb") or 0) / 1024 <= a.max_gb]
    if a.limit:
        pairs = pairs[:a.limit]
    done = {}
    if os.path.exists(RESULTS):
        done = {r["raw"]: r for r in json.load(open(RESULTS))}
    todo = [p for p in pairs if p["raw"] not in done]
    print(f"corpus pairs: {len(pairs)}  already done: {len(pairs)-len(todo)}  to run: {len(todo)}  "
          f"workers={a.workers} timeout={a.timeout}s", flush=True)
    results = list(done.values())
    def flush():
        json.dump(results, open(RESULTS, "w"), indent=2)
    with ThreadPoolExecutor(max_workers=a.workers) as ex:
        futs = {ex.submit(run_one, p, a.timeout): p for p in todo}
        for i, f in enumerate(as_completed(futs), 1):
            r = f.result(); results.append(r); flush()
            c = r.get("compare", {})
            extra = f" exact={c.get('exact_frac')} refContained={c.get('ref_contained_frac')} spectra={c.get('ref_spectra')}" if c else ""
            print(f"[{i}/{len(todo)}] {r['status']:13s} {r.get('raw_mb'):>7}MB {r['name']}"
                  f"  ({r.get('convert_secs','?')}s){extra}", flush=True)
    write_report(results)
    bad = [r for r in results if r["status"] != "PASS"]
    print(f"\nDONE. PASS {len(results)-len(bad)}/{len(results)}. report: {os.path.join(OUTDIR,'report.md')}")
    sys.exit(0 if not bad else 1)

def write_report(results):
    import collections
    by = collections.Counter(r["status"] for r in results)
    lines = ["# mzPeak E2E corpus comparison (TRFP vs mzML2mzPeak reference)", ""]
    lines.append(f"Pairs run: **{len(results)}** · " + " · ".join(f"{k}: {v}" for k, v in sorted(by.items())))
    lines += ["", "| status | file | RAW MB | spectra | exact% | ref-contained% | convert s | validate |",
              "|---|---|--:|--:|--:|--:|--:|---|"]
    for r in sorted(results, key=lambda r: (r["status"] != "PASS", -(r.get("raw_mb") or 0))):
        c = r.get("compare", {}) or {}
        lines.append(f"| {r['status']} | {r['name']} | {r.get('raw_mb','')} | {c.get('ref_spectra','')} | "
                     f"{c.get('exact_frac','')} | {c.get('ref_contained_frac','')} | {r.get('convert_secs','')} | "
                     f"{(r.get('validate','') or '')[:34]} |")
    nonpass = [r for r in results if r["status"] != "PASS"]
    if nonpass:
        lines += ["", "## Non-PASS detail", ""]
        for r in nonpass:
            lines.append(f"- **{r['name']}** [{r['status']}] — {r.get('detail','')}  compare={json.dumps(r.get('compare',{}))[:300]}")
    open(os.path.join(OUTDIR, "report.md"), "w").write("\n".join(lines) + "\n")

if __name__ == "__main__":
    main()
