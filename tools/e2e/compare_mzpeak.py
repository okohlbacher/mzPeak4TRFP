#!/usr/bin/env python3.11
"""Compare two mzPeak archives semantically: ours (TRFP) vs a reference (mzML2mzPeak corpus).

Aligns spectra by nativeID, unions each spectrum's signal across spectra_data + spectra_peaks
(decoding point, delta-chunk, and numpress-linear chunk layouts), and reports per-spectrum
(m/z, intensity) multiset agreement plus ms_level / polarity / RT agreement.

The signal decode/keying is NumPy-vectorized (Arrow column access, per-spectrum np.unique multiset
keys) so multi-GB facets complete; the per-point Python path it replaced timed out on the 2.1 GB file.

Usage:  compare_mzpeak.py OURS.mzpeak REFERENCE.mzpeak [--mz-dp 5] [--json]
Exit 0 = equivalent within tolerance, 1 = differences, 2 = engine error.
"""
import sys, json, zipfile, io, struct, collections, argparse
import numpy as np

def _read(zp, name):
    import pyarrow.parquet as pq
    if name not in zp.namelist():
        return None
    return pq.read_table(io.BytesIO(zp.read(name))).to_pylist()

def _batches(zp, name):
    """Arrow RecordBatches for a facet (column access avoids per-row Python materialization)."""
    import pyarrow.parquet as pq
    if name not in zp.namelist():
        return
    pf = pq.ParquetFile(io.BytesIO(zp.read(name)))
    for b in pf.iter_batches(batch_size=65536):
        yield b

def _np_decode(buf, kind):
    import pynumpress
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
        return np.array(out, dtype=np.float64)
    a = np.frombuffer(b, dtype=np.uint8)
    dec = pynumpress.decode_linear if kind == "linear" else pynumpress.decode_slof
    return np.asarray(dec(a), dtype=np.float64)

def _struct_fields(col):
    return {f.name for f in col.type}

def _chunk_mz(npb, vals, start):
    """m/z values for one chunk row: numpress-linear bytes, else delta from start, else absolute."""
    if npb:                                            # numpress-linear (CURIE MS:1002312)
        return _np_decode(npb, "linear")
    if vals:                                           # delta: start + consecutive (non-null) deltas
        d = np.fromiter((x for x in vals if x is not None), dtype=np.float64)
        if start is None:
            return np.empty(0, dtype=np.float64)
        return np.concatenate(([start], start + np.cumsum(d)))
    return np.array([start], dtype=np.float64) if start is not None else np.empty(0, dtype=np.float64)

def _chunk_intensity(inten, slof):
    """intensity values for one chunk row: plain list, else SLOF bytes (reference)."""
    if inten:
        return np.fromiter((x for x in inten if x is not None), dtype=np.float64)
    if slof:                                           # reference Numpress-SLOF intensity (MS:1002314)
        return _np_decode(slof, "slof")
    return np.empty(0, dtype=np.float64)

def _decode_chunk_row(npb, vals, start, inten, slof):
    """(mz, intensity) arrays for one chunk row across point/delta/numpress (linear m/z, SLOF intensity)."""
    mz = _chunk_mz(npb, vals, start)
    it = _chunk_intensity(inten, slof)
    if mz.size == it.size + 1:                         # pynumpress decode can prepend a phantom anchor
        mz = mz[1:]
    elif it.size == mz.size + 1:
        it = it[1:]
    n = min(mz.size, it.size)
    return mz[:n], it[:n]

def _accumulate(batches, raw):
    """Append (mz_array, intensity_array) signal segments per spectrum_index from a facet's batches."""
    for b in batches:
        names = set(b.schema.names)
        if "point" in names:
            col = b.column(b.schema.get_field_index("point"))
            si = col.field("spectrum_index").to_numpy(zero_copy_only=False)
            mz = col.field("mz").to_numpy(zero_copy_only=False)
            it = col.field("intensity").to_numpy(zero_copy_only=False)
            order = np.argsort(si, kind="stable")
            si_s, mz_s, it_s = si[order], mz[order], it[order]
            cuts = np.flatnonzero(np.diff(si_s)) + 1
            for smz, sit, ssi in zip(np.split(mz_s, cuts), np.split(it_s, cuts), np.split(si_s, cuts)):
                if ssi.size:
                    raw[int(ssi[0])].append((smz.astype(np.float64), sit.astype(np.float64)))
        elif "chunk" in names:
            col = b.column(b.schema.get_field_index("chunk"))
            fields = _struct_fields(col)
            si = col.field("spectrum_index").to_numpy(zero_copy_only=False)
            start = col.field("mz_chunk_start").to_pylist()
            npb = col.field("mz_numpress_linear_bytes").to_pylist() if "mz_numpress_linear_bytes" in fields else [None] * len(si)
            vals = col.field("mz_chunk_values").to_pylist() if "mz_chunk_values" in fields else [None] * len(si)
            inten = col.field("intensity").to_pylist() if "intensity" in fields else [None] * len(si)
            slof = col.field("intensity_numpress_slof_bytes").to_pylist() if "intensity_numpress_slof_bytes" in fields else [None] * len(si)
            for i in range(len(si)):
                mz_a, it_a = _decode_chunk_row(npb[i], vals[i], start[i], inten[i], slof[i])
                if mz_a.size:
                    raw[int(si[i])].append((mz_a, it_a))

def _signal_by_index(zp):
    """index -> list of (mz_array, intensity_array) segments, unioning spectra_data + spectra_peaks."""
    raw = collections.defaultdict(list)
    _accumulate(_batches(zp, "spectra_data.parquet"), raw)
    _accumulate(_batches(zp, "spectra_peaks.parquet"), raw)
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

def _key_counter(segments, mz_dp):
    """Multiset Counter of (round(mz,mz_dp), f32(intensity)) over nonzero points, NumPy-vectorized."""
    if not segments:
        return collections.Counter()
    mz = np.concatenate([s[0] for s in segments]) if len(segments) > 1 else segments[0][0]
    it = np.concatenate([s[1] for s in segments]) if len(segments) > 1 else segments[0][1]
    nz = it != 0.0
    if not nz.any():
        return collections.Counter()
    mz = np.round(mz[nz], mz_dp)
    it32 = it[nz].astype(np.float32)
    pairs = np.empty(mz.size, dtype=[("mz", "f8"), ("it", "f4")])
    pairs["mz"] = mz
    pairs["it"] = it32
    uniq, cnt = np.unique(pairs, return_counts=True)
    return collections.Counter(dict(zip(
        ((float(u["mz"]), float(u["it"])) for u in uniq), cnt.tolist())))

def compare(ours_path, ref_path, mz_dp=5):
    zo, zr = zipfile.ZipFile(ours_path), zipfile.ZipFile(ref_path)
    om, rm = _meta(_read(zo, "spectra_metadata.parquet")), _meta(_read(zr, "spectra_metadata.parquet"))
    osig = _signal_by_index(zo)
    rsig = _signal_by_index(zr)

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
