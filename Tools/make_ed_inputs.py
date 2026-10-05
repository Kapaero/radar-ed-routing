"""Builds Data/derived/ed_inputs.json, the empirical inputs of the ED simulation, from NHAMCS ED 2016-2019 (weighted):
hourly arrival profile, acuity / ambulance / imaging / admission shares, length-of-visit and boarding quantiles.
Usage: python make_ed_inputs.py <nhamcs dir> <output json>"""
import io
import json
import re
import sys
import zipfile
from pathlib import Path

import numpy as np
import pandas as pd

SRC = Path(sys.argv[1])
OUT = Path(sys.argv[2])
VARS = ["ARRTIME", "IMMEDR", "ARREMS", "ANYIMAGE", "ADMITHOS", "OBSHOS", "LOV", "WAITTIME", "BOARDED", "PATWT"]
GRID = np.round(np.linspace(0.01, 0.99, 99), 2)


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
w = d.PATWT
critical = d.IMMEDR.isin([1, 2])
triaged = d.IMMEDR.isin([1, 2, 3, 4, 5])
admitted = (d.ADMITHOS == 1) | (d.OBSHOS == 1)
ambulance_known = d.ARREMS.isin([1, 2])


def share(mask, base):
    return round(float(w[mask & base].sum() / w[base].sum()), 4)


def quantiles(values, weights):
    ok = values.notna() & (values >= 0)
    x, wt = values[ok].to_numpy(float), weights[ok].to_numpy(float)
    order = np.argsort(x)
    cw = np.cumsum(wt[order]) / wt.sum()
    return [round(float(np.interp(p, cw, x[order])), 1) for p in GRID]


hour = pd.to_numeric(d.ARRTIME, errors="coerce") // 100
prof = w[hour.between(0, 23)].groupby(hour[hour.between(0, 23)]).sum()
relative = (prof / prof.mean()).reindex(range(24)).fillna(0)

inputs = {
    "source": "NHAMCS ED public-use files 2016-2019 (CDC/NCHS), weighted by PATWT",
    "hourlyRelative": [round(float(v), 4) for v in relative],
    "criticalShare": share(critical, triaged),
    "ambulanceShareCritical": share(d.ARREMS == 1, critical & ambulance_known),
    "ambulanceShareOther": share(d.ARREMS == 1, ~critical & ambulance_known),
    "imagingShareCritical": share(d.ANYIMAGE == 1, critical),
    "imagingShareOther": share(d.ANYIMAGE == 1, ~critical),
    "admitShareCritical": share(admitted, critical),
    "admitShareOther": share(admitted, ~critical),
    "quantileLevels": [float(p) for p in GRID],
    "lovCriticalAdmitted": quantiles(d.LOV[critical & admitted], w[critical & admitted]),
    "lovCriticalDischarged": quantiles(d.LOV[critical & ~admitted], w[critical & ~admitted]),
    "lovOtherAdmitted": quantiles(d.LOV[~critical & admitted], w[~critical & admitted]),
    "lovOtherDischarged": quantiles(d.LOV[~critical & ~admitted], w[~critical & ~admitted]),
    "boarded": quantiles(d.BOARDED[(d.year == 2019) & admitted], w[(d.year == 2019) & admitted]),
    "waitTimeAll": quantiles(d.WAITTIME, w),
}
OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(json.dumps(inputs, indent=1), encoding="utf-8")
print({k: v for k, v in inputs.items() if not isinstance(v, list)})
print("LOV medians (min):", {k: inputs[k][49] for k in inputs if k.startswith("lov")}, "boarded median", inputs["boarded"][49])
print("written", OUT)
