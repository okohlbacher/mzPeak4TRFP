#!/usr/bin/env python3.11
"""Compare two mzPeak archives semantically: ours (TRFP) vs a reference (mzML2mzPeak corpus).

Aligns spectra by nativeID, unions each spectrum's signal across spectra_data + spectra_peaks
(decoding point, delta-chunk, and numpress-linear chunk layouts), and reports per-spectrum
(m/z, intensity) multiset agreement plus ms_level / polarity / RT agreement.

Usage:  compare_mzpeak.py OURS.mzpeak REFERENCE.mzpeak [--mz-dp 5] [--json]
Exit 0 = equivalent within tolerance, 1 = differences, 2 = engine error.
"""
import sys, json, zipfile, io, struct, collections, argparse

def _read(zp, name):
    import pyarrow.parquet as pq
    if name not in zp.namelist():
        return None
    return pq.read_table(io.BytesIO(zp.read(name))).to_pylist()

def _rows(zp, name):
    """Memory-bounded row iterator for large facets (avoids materializing the whole facet)."""
    import pyarrow.parquet as pq
    if name not in zp.namelist():
        return
    pf = pq.ParquetFile(io.BytesIO(zp.read(name)))
    for b in pf.iter_batches(batch_size=65536):
        for r in b.to_pylist():
            yield r

def _f32(x):
    return struct.unpack("f", struct.pack("f", float(x)))[0]

def _np_decode(buf, kind):
    import pynumpress, numpy as np
    b = bytes(buf)
    if kind == "linear" and len(b) <= 16:
        # pynumpress 0.0.9 rejects short (<=2-value) linear streams; decode them per canonical:
        # 8-byte BE fixed point, then up to two 4-byte LE int32 values.
        fp = struct.unpack(">d", b[0:8])[0]
        out = []
        if len(b) >= 12:
            out.append(struct.unpack("<i", b[8:12])[0] / fp)
        if len(b) >= 16:
            out.append(struct.unpack("<i", b[12:16])[0] / fp)
        return out
    a = np.frombuffer(b, dtype=np.uint8)
    return list((pynumpress.decode_linear if kind == "linear" else pynumpress.decode_slof)(a))

def _chunk_mz(c):
    """m/z values for one chunk row: numpress-linear bytes, else delta from start, else absolute."""
    npb = c.get("mz_numpress_linear_bytes")
    if npb:                                            # numpress-linear (CURIE MS:1002312)
        return _np_decode(npb, "linear")
    vals = c.get("mz_chunk_values")
    start = c.get("mz_chunk_start")
    if vals:                                           # delta: start + consecutive (non-null) deltas
        out, mz = ([start] if start is not None else []), start
        for d in vals:
            if d is None:                              # null-marked (zero-stripped) — Phase 4
                continue
            mz += d
            out.append(mz)
        return out
    return [start] if start is not None else []

def _chunk_intensity(c):
    """intensity values for one chunk row: plain list, else SLOF bytes (reference)."""
    inten = c.get("intensity")
    if inten:
        return [x for x in inten if x is not None]
    slof = c.get("intensity_numpress_slof_bytes")
    if slof:                                           # reference Numpress-SLOF intensity (MS:1002314)
        return _np_decode(slof, "slof")
    return []

def _decode_chunk_row(c):
    """Yield (mz, intensity) for one chunk row across point/delta/numpress (linear m/z, SLOF intensity)."""
    mzs = _chunk_mz(c)
    inten = _chunk_intensity(c)
    if len(mzs) == len(inten) + 1:                   # pynumpress decode can prepend a phantom anchor
        mzs = mzs[1:]
    elif len(inten) == len(mzs) + 1:
        inten = inten[1:]
    for i in range(min(len(mzs), len(inten))):
        yield float(mzs[i]), inten[i]

def _signal_by_index(rows_data, rows_peaks):
    """index -> Counter[(mz_key, f32_intensity)] over nonzero points, unioning data + peaks."""
    sig = collections.defaultdict(collections.Counter)
    raw = collections.defaultdict(list)   # index -> [(mz,intensity)]
    if rows_data:
        for r in rows_data:
            if "point" in r and r["point"] is not None:
                p = r["point"]
                raw[p["spectrum_index"]].append((p["mz"], p["intensity"]))
            elif "chunk" in r and r["chunk"] is not None:
                c = r["chunk"]
                raw[c["spectrum_index"]].extend(_decode_chunk_row(c))
    if rows_peaks:
        for r in rows_peaks:
            if r.get("point") is not None:
                p = r["point"]
                raw[p["spectrum_index"]].append((p["mz"], p["intensity"]))
            elif r.get("chunk") is not None:
                c = r["chunk"]
                raw[c["spectrum_index"]].extend(_decode_chunk_row(c))
    return raw

def _meta(rows):
    """index -> dict(id, ms_level, polarity, rt); only spectrum struct rows."""
    out = {}
    for r in rows or []:
        s = r.get("spectrum")
        if not s:
            continue
        out[s["index"]] = {
            "id": s.get("id"),
            "ms_level": s.get("MS_1000511_ms_level"),
            "polarity": s.get("MS_1000465_scan_polarity"),
            "rt": s.get("time"),
        }
    return out

def _key_counter(pairs, mz_dp):
    c = collections.Counter()
    for mz, it in pairs:
        if it == 0:
            continue
        c[(round(float(mz), mz_dp), _f32(it))] += 1
    return c

def compare(ours_path, ref_path, mz_dp=5):
    zo, zr = zipfile.ZipFile(ours_path), zipfile.ZipFile(ref_path)
    om, rm = _meta(_read(zo, "spectra_metadata.parquet")), _meta(_read(zr, "spectra_metadata.parquet"))
    osig = _signal_by_index(_rows(zo, "spectra_data.parquet"), _rows(zo, "spectra_peaks.parquet"))
    rsig = _signal_by_index(_rows(zr, "spectra_data.parquet"), _rows(zr, "spectra_peaks.parquet"))

    # align by nativeID where present, else by index
    def id2idx(m):
        return {v["id"]: i for i, v in m.items() if v.get("id")}
    oi, ri = id2idx(om), id2idx(rm)
    use_id = len(oi) == len(om) and len(ri) == len(rm) and len(om) and len(rm)
    if use_id:
        shared = sorted(set(oi) & set(ri))
        pairs = [(oi[k], ri[k]) for k in shared]
        align = "nativeID"
    else:
        shared = sorted(set(om) & set(rm))
        pairs = [(i, i) for i in shared]
        align = "index"

    n_exact = n_mz = n_ref_subset = 0
    ms_ok = pol_ok = rt_ok = 0
    mism = []
    for oidx, ridx in pairs:
        oc = _key_counter(osig.get(oidx, []), mz_dp)
        rc = _key_counter(rsig.get(ridx, []), mz_dp)
        if oc == rc:
            n_exact += 1
        oc_mz = collections.Counter(k[0] for k in oc.elements())
        rc_mz = collections.Counter(k[0] for k in rc.elements())
        if oc_mz == rc_mz:
            n_mz += 1
        if not (rc - oc):           # ref multiset contained in ours (we may emit profile too)
            n_ref_subset += 1
        elif len(mism) < 5:
            mism.append({"ours_idx": oidx, "ref_idx": ridx,
                         "ours_pts": sum(oc.values()), "ref_pts": sum(rc.values())})
        omd, rmd = om.get(oidx, {}), rm.get(ridx, {})
        if omd.get("ms_level") == rmd.get("ms_level"): ms_ok += 1
        if omd.get("polarity") is None or rmd.get("polarity") is None or omd["polarity"] == rmd["polarity"]: pol_ok += 1
        if omd.get("rt") is not None and rmd.get("rt") is not None and abs(omd["rt"] - rmd["rt"]) < 1e-3: rt_ok += 1

    n = len(pairs)
    out = {
        "ours_spectra": len(om), "ref_spectra": len(rm),
        "spectrum_count_ok": len(om) == len(rm),
        "aligned_by": align, "aligned_spectra": n,
        "exact_multiset": n_exact, "exact_frac": round(n_exact / n, 4) if n else None,
        "mz_set_match": n_mz, "mz_frac": round(n_mz / n, 4) if n else None,
        "ref_contained_in_ours": n_ref_subset, "ref_contained_frac": round(n_ref_subset / n, 4) if n else None,
        "ms_level_match": ms_ok, "polarity_match": pol_ok, "rt_match": rt_ok,
        "mismatch_examples": mism,
    }
    # verdict: same spectrum set, and every reference spectrum's signal reproduced in ours
    out["verdict"] = "PASS" if (out["spectrum_count_ok"] and n and n_ref_subset == n
                                and ms_ok == n and rt_ok == n) else "DIFF"
    return out

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("ours"); ap.add_argument("ref")
    ap.add_argument("--mz-dp", type=int, default=5)
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()
    try:
        r = compare(a.ours, a.ref, a.mz_dp)
    except Exception as e:
        print(json.dumps({"verdict": "ERROR", "error": f"{type(e).__name__}: {e}"}))
        sys.exit(2)
    print(json.dumps(r, indent=2) if a.json else json.dumps(r))
    sys.exit(0 if r["verdict"] == "PASS" else 1)

if __name__ == "__main__":
    main()
