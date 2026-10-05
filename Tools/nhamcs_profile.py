"""Arrival-hour profile, triage-level shares and ambulance share of US emergency department visits
from the NHAMCS ED public-use files (fixed-width ASCII; column positions read from the CDC Stata dictionaries).
Usage: python nhamcs_profile.py <dir with edYYYY.zip and eddictYYYY.dct> <output csv prefix>"""
import io
import re
import sys
import zipfile
from pathlib import Path

import numpy as np
import pandas as pd

SRC = Path(sys.argv[1])
OUT = sys.argv[2]
VARS = ["VDAYR", "ARRTIME", "IMMEDR", "ARREMS", "PATWT"]


def positions(dct_path):
    pos = {}
    for line in dct_path.read_text(encoding="latin-1").splitlines():
        m = re.match(r"\s*\w+\s+(\w+)\s+(\d+)(?:-(\d+))?", line)
        if m and m.group(1) in VARS:
            a = int(m.group(2))
            b = int(m.group(3) or a)
            pos[m.group(1)] = (a - 1, b)
    return pos


frames = []
for year in (2016, 2017, 2018, 2019):
    zip_path = next(p for p in SRC.iterdir() if p.suffix.lower() == ".zip" and str(year) in p.name)
    pos = positions(SRC / f"eddict{year}.dct")
    with zipfile.ZipFile(zip_path) as z:
        raw = z.read(z.namelist()[0]).decode("latin-1")
    colspecs = [pos[v] for v in VARS]
    df = pd.read_fwf(io.StringIO(raw), colspecs=colspecs, names=VARS, dtype=str)
    df["year"] = year
    frames.append(df)
d = pd.concat(frames, ignore_index=True)
d["PATWT"] = pd.to_numeric(d.PATWT, errors="coerce")
d["IMMEDR"] = pd.to_numeric(d.IMMEDR, errors="coerce")
d["ARREMS"] = pd.to_numeric(d.ARREMS, errors="coerce")
t = pd.to_numeric(d.ARRTIME, errors="coerce")
d["hour"] = np.where((t >= 0) & (t <= 2359), (t // 100).astype("Int64"), pd.NA)

print("records by year:", d.groupby("year").size().to_dict())
print("weighted visits by year (millions):", (d.groupby("year").PATWT.sum() / 1e6).round(1).to_dict())

h = d.dropna(subset=["hour"])
prof = h.groupby("hour").PATWT.sum()
prof = prof / prof.sum()
rel = prof / prof.mean()
print("\narrival profile (share of daily visits per hour; relative to the hourly mean):")
for hr in range(24):
    print(f"  {hr:02d}:00  {prof.get(hr, 0):.4f}  x{rel.get(hr, 0):.2f}")
print("peak hour:", int(rel.idxmax()), "peak/mean ratio:", round(float(rel.max()), 2))

tri = d[d.IMMEDR.isin([1, 2, 3, 4, 5])]
share = tri.groupby("IMMEDR").PATWT.sum() / tri.PATWT.sum()
print("\ntriage level shares among triaged visits (weighted):", share.round(4).to_dict())
print("immediate+emergent share:", round(float(share.loc[[1, 2]].sum()), 4))
print("not triaged / unknown share of all visits:", round(float(d[~d.IMMEDR.isin([1, 2, 3, 4, 5])].PATWT.sum() / d.PATWT.sum()), 4))
amb = d[d.ARREMS.isin([1, 2])]
print("ambulance arrival share (ARREMS=1 among 1/2):", round(float(amb[amb.ARREMS == 1].PATWT.sum() / amb.PATWT.sum()), 4))
crit = tri[tri.IMMEDR.isin([1, 2]) & tri.ARREMS.isin([1, 2])]
print("ambulance share among immediate+emergent:", round(float(crit[crit.ARREMS == 1].PATWT.sum() / crit.PATWT.sum()), 4))

pd.DataFrame({"hour": range(24), "share": [prof.get(i, 0) for i in range(24)], "relative": [rel.get(i, 0) for i in range(24)]}).to_csv(OUT + "_hourly.csv", index=False)
share.rename("share").to_csv(OUT + "_triage.csv")
