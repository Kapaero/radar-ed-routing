"""Writes one validation config per Jülich unidirectional run (uo-<b_entrance>-<b_cor>-<b_exit>.txt) into Runs/val_juelich.
Geometry inferred from the trajectories (frame rate 16 fps from the data archive, DOI 10.34735/ped.2009.14): the lateral
spread of the positions is constant from y = 4 m to y = -4 m (the corridor of width b_cor, x = 0 .. b_cor) and wider
upstream (the 4 m passage of Zhang et al. 2011); the passage walls are placed 0.25 m beyond the outermost positions
observed in it over all runs with the same corridor width (0.5 and 99.5 percentiles).
Free speeds: Weidmann (1993) by default, as in the hospital runs; the sensitivity run uses the participants' own
distribution, N(1.455, 0.189) m/s from their median corridor speeds in the free-flow runs (b_entrance <= 0.8 m).
Usage: python juelich_configs.py [out_dir_name] [seed] [speed_mean speed_sd speed_max]"""
import json
import re
import sys
from pathlib import Path

import numpy as np
import pandas as pd

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "Data" / "external" / "juelich" / "uo"
OUT = ROOT / "Runs" / (sys.argv[1] if len(sys.argv) > 1 else "val_juelich")
MARGIN = 0.25
seed = int(sys.argv[2]) if len(sys.argv) > 2 else 1
speed = dict(zip(("speedMean", "speedSd", "speedMax"), map(float, sys.argv[3:6]))) if len(sys.argv) > 5 else {}

runs = []
for f in sorted(SRC.glob("uo-*.txt")):
    m = re.match(r"uo-(\d{3})-(\d{3})-(\d{3})(v\d+)?\.txt", f.name)
    d = pd.read_csv(f, sep=r"\s+", header=None, names=["id", "frame", "x", "y", "z"], comment="#")
    passage = d[(d.y > 450) & (d.y < 780)]
    lo, hi = np.percentile(passage.x, [0.5, 99.5]) / 100
    runs.append(dict(name=f.stem, path=f, b_entrance=int(m[1]) / 100, b_cor=int(m[2]) / 100, b_exit=int(m[3]) / 100, lo=lo, hi=hi))

extent = {}
for r in runs:
    lo, hi = extent.get(r["b_cor"], (np.inf, -np.inf))
    extent[r["b_cor"]] = (min(lo, r["lo"]), max(hi, r["hi"]))

OUT.mkdir(parents=True, exist_ok=True)
for r in runs:
    lo, hi = extent[r["b_cor"]]
    cfg = {
        "runId": r["name"],
        "trajectoryPath": r["path"].as_posix(),
        "frameRate": 16,
        "corridorWidth": r["b_cor"],
        "exitWidth": r["b_exit"],
        "corridorEntryY": 4.0,
        "exitY": -4.0,
        "passageEntryY": 8.0,
        "passageLeftX": round(lo - MARGIN, 2),
        "passageRightX": round(hi + MARGIN, 2),
        "dt": 0.04,
        "seed": seed,
        **speed,
    }
    (OUT / f"{r['name']}.json").write_text(json.dumps(cfg, indent=1))
print(f"{len(runs)} configs in {OUT}; passage walls by corridor width:",
      {b: (round(lo - MARGIN, 2), round(hi + MARGIN, 2)) for b, (lo, hi) in sorted(extent.items())})
