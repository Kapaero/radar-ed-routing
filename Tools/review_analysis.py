"""Ablation (C1w: true free width, no density) and sensitivity to the time needed to push a trolley aside (NUK).
Usage: python Tools/review_analysis.py   (after running Runs/review with Tools/run_configs.sh)"""
import filecmp
import numpy as np
import pandas as pd
from scipy import stats


def means(run):
    t = pd.read_csv(run + "/trips.csv")
    t = t[(t.t_start >= 7200) & (t.kind != "ward_from_corridor")]
    t["d"] = t.t_end - t.t_start
    return t.d.mean(), t[t.stretcher == 1].d.mean()


same = sum(filecmp.cmp(f"Runs/review/{sc}_OracleWidthOnly_s{s:02d}/trips.csv", f"Runs/final/{sc}_Oracle_s{s:02d}/trips.csv", shallow=False)
           for sc in ["WUS", "WUK", "NUK"] for s in range(1, 11))
print(f"C1w identical to C1 in {same} of 30 runs")
for mw, pre in [(60, "Runs/review/NUK60"), (120, "Runs/final/NUK"), (240, "Runs/review/NUK240")]:
    base = np.array([means(f"{pre}_NoSensing_s{s:02d}")[1] for s in range(1, 11)])
    for cond in ["Oracle", "DopplerStatic"]:
        d = np.array([means(f"{pre}_{cond}_s{s:02d}")[1] for s in range(1, 11)]) - base
        h = stats.t.ppf(.975, 9) * d.std(ddof=1) / np.sqrt(10)
        print(f"push aside {mw:3d} s, {cond:13}: stretcher trip {d.mean():+.1f} +- {h:.1f} s vs C0 ({base.mean():.1f} s)")
