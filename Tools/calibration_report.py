"""Calibration report for runs of the operating scenarios: occupancy (census), share of patients present who wait on a
corridor trolley (compared with RCEM corridor care), time to first physician (compared with NHAMCS WAITTIME, KS
distance on the quantile grid) and staff walking distance per hour (compared with the pedometer studies).
Usage: python calibration_report.py <warmup_seconds> <run_dir> [<run_dir> ...]"""
import json
import sys
from pathlib import Path

import numpy as np
import pandas as pd

ROOT = Path(__file__).resolve().parents[1]
ed = json.load(open(ROOT / "Data" / "derived" / "ed_inputs.json"))
ref, levels = np.array(ed["waitTimeAll"]), np.array(ed["quantileLevels"])
warmup = float(sys.argv[1])

for run in map(Path, sys.argv[2:]):
    c = pd.read_csv(run / "census.csv")
    c = c[c.t >= warmup]
    present = (c.waiting_room + c.ambulance_bay + c.corridor + c.to_cubicle + c.in_cubicle + c.boarding + c.imaging
               + c.awaiting_porter)
    share = c.corridor / present
    p = pd.read_csv(run / "patients.csv")
    a = p[(p.initial == 0) & (p.t_arrival >= warmup) & (p.t_first_physician > 0)]
    ttp = (a.t_first_physician - a.t_arrival) / 60
    ks = np.max(np.abs(np.array([(ttp <= v).mean() for v in ref]) - levels)) if len(ttp) else float("nan")
    boarded = int(p.boarded_in_corridor.sum()) if "boarded_in_corridor" in p else 0
    s = pd.read_csv(run / "staff.csv") if (run / "staff.csv").exists() else None
    walk = ""
    if s is not None and "km_per_h" in s:
        walk = "; km/h " + ", ".join(f"{r} {v:.2f}" for r, v in s.groupby("role").km_per_h.mean().items())
    print(f"{run.name:14}: present {present.mean():5.1f}, corridor {c.corridor.mean():4.1f} -> share {share.mean():.3f} "
          f"(p10 {share.quantile(.1):.3f}, p90 {share.quantile(.9):.3f}); waiting room {c.waiting_room.mean():4.1f}; "
          f"transport queue {c.transport_queue.mean():.2f}; boarded in corridor {boarded}; "
          f"TTP n={len(ttp)} median {ttp.median():5.1f} q90 {ttp.quantile(.9):6.1f} KS {ks:.2f}{walk}")
