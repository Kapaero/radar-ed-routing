"""Patient-flow quantities for the ED simulation from NHAMCS ED 2016-2019 (weighted by PATWT):
length of visit (LOV) and waiting time (WAITTIME) distributions by acuity and disposition, imaging shares,
admission share, and boarding time (2019 only). Writes empirical quantiles used by the simulation.
Usage: python nhamcs_flow.py <dir with edYYYY.zip and eddictYYYY.dct> <output prefix>"""
import io
import re
import sys
import zipfile
from pathlib import Path

import numpy as np
import pandas as pd

SRC = Path(sys.argv[1])
OUT = sys.argv[2]
VARS = ["WAITTIME", "LOV", "IMMEDR", "ARREMS", "ANYIMAGE", "XRAY", "CATSCAN", "ULTRASND", "MRI",
        "ADMITHOS", "OBSHOS", "BOARDED", "PATWT"]


def positions(dct_path):
    pos = {}
    for line in dct_path.read_text(encoding="latin-1").splitlines():
        m = re.match(r"\s*\w+\s+(\w+)\s+(\d+)(?:-(\d+))?", line)
        if m and m.group(1) in VARS:
            pos[m.group(1)] = (int(m.group(2)) - 1, int(m.group(3) or m.group(2)))
    return pos


frames = []
for year in (2016, 2017, 2018, 2019):
    zip_path = next(p for p in SRC.iterdir() if p.suffix.lower() == ".zip" and str(year) in p.name)
    pos = positions(SRC / f"eddict{year}.dct")
    names = [v for v in VARS if v in pos]
    with zipfile.ZipFile(zip_path) as z:
        raw = z.read(z.namelist()[0]).decode("latin-1")
    df = pd.read_fwf(io.StringIO(raw), colspecs=[pos[v] for v in names], names=names, dtype=str)
    df = df.apply(pd.to_numeric, errors="coerce")
    df["year"] = year
    frames.append(df)
d = pd.concat(frames, ignore_index=True)
d["critical"] = d.IMMEDR.isin([1, 2])
d["admitted"] = (d.ADMITHOS == 1) | (d.OBSHOS == 1)
w = d.PATWT


def wshare(mask, base=None):
    base = np.ones(len(d), bool) if base is None else base
    return float(w[mask & base].sum() / w[base].sum())


def wquant(x, weights, qs):
    ok = x.notna() & (x >= 0)
    x, weights = x[ok].to_numpy(float), weights[ok].to_numpy(float)
    order = np.argsort(x)
    x, weights = x[order], weights[order]
    cw = np.cumsum(weights) / weights.sum()
    return [float(np.interp(q, cw, x)) for q in qs]


print("shares (weighted):")
for name, mask in [("any imaging", d.ANYIMAGE == 1), ("x-ray", d.XRAY == 1), ("CT", d.CATSCAN == 1),
                   ("ultrasound", d.ULTRASND == 1), ("MRI", d.MRI == 1), ("admitted or observation", d.admitted)]:
    print(f"  {name:24} all {wshare(mask):.3f}   critical {wshare(mask, d.critical.values):.3f}   other {wshare(mask, (~d.critical).values):.3f}")

qs = [0.05, 0.1, 0.25, 0.5, 0.75, 0.9, 0.95]
rows = []
for var in ("WAITTIME", "LOV"):
    for label, mask in [("all", np.ones(len(d), bool)), ("critical", d.critical.values), ("other", (~d.critical).values),
                        ("admitted", d.admitted.values), ("discharged", (~d.admitted).values)]:
        q = wquant(d.loc[mask, var], w[mask], qs)
        rows.append([var, label] + q)
        print(f"{var:8} {label:10} " + "  ".join(f"q{int(p*100):02d}={v:6.0f}" for p, v in zip(qs, q)))
b = d[(d.year == 2019) & d.admitted]
qb = wquant(b.BOARDED, b.PATWT, qs)
rows.append(["BOARDED_2019", "admitted"] + qb)
print("BOARDED (2019, admitted, min) " + "  ".join(f"q{int(p*100):02d}={v:6.0f}" for p, v in zip(qs, qb)))
pd.DataFrame(rows, columns=["variable", "group"] + [f"q{int(p*100):02d}" for p in qs]).to_csv(OUT + "_quantiles.csv", index=False)

# empirical LOV distribution (minutes) by acuity and disposition, for sampling in the simulation
grid = np.linspace(0.01, 0.99, 99)
out = {}
for label, mask in [("critical_admitted", d.critical & d.admitted), ("critical_discharged", d.critical & ~d.admitted),
                    ("other_admitted", ~d.critical & d.admitted), ("other_discharged", ~d.critical & ~d.admitted)]:
    out[label] = wquant(d.loc[mask, "LOV"], w[mask], grid)
pd.DataFrame(out, index=np.round(grid, 2)).to_csv(OUT + "_lov_quantiles.csv", index_label="p")
print("written", OUT + "_quantiles.csv", OUT + "_lov_quantiles.csv")
