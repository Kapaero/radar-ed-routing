"""Final experiment: information conditions C0-C4 x scenarios (WUS, WUK, NUK; see make_final_configs.py) x seeds 1-10,
with common random numbers
(the same arrivals, closures and incidents in every condition of a seed). Everything after the 2 h warm-up.

Routing outcomes, from trips.csv (routed patient trips: inbound, imaging_out, imaging_back, outbound):
- mean trip duration and walked distance, all trips and trips under way while a corridor closure is active;
- on-site blocked encounters (the patient reaches a closure it was routed into) per 100 trips.
Run means are compared with C0 (no sensing) seed by seed: mean paired difference, 95% t-interval, paired t-test.

Sensing outcomes, from cells.csv of the C3 runs (the radar model is the same in every condition):
- density error of the Doppler, fused and calibrated (C4 knots) estimates against the true cell density;
- closure detection: delay from the start of a closure to the first sensed free width below the stretcher width in
  the closed cell; false "blocked" flags (sensed free width below 1.2 m while the true one is not).
Output: Docs/results/final_runs.csv, final_paired.csv, final_sensing.csv, final_detection.csv, final_routing.png.
Usage: python final_analysis.py"""
import json
import re
from pathlib import Path

import matplotlib
import numpy as np
import pandas as pd
from scipy import stats

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
DIRS = [ROOT / "Runs" / "final", ROOT / "Runs" / "final_c4"]
OUT = ROOT / "Docs" / "results"
WARMUP = 7200.0
STRETCHER_WIDTH = 1.2
CONDITIONS = {"NoSensing": "C0", "Oracle": "C1", "DopplerOnly": "C2", "DopplerStatic": "C3", "Calibrated": "C4"}
knots_path = ROOT / "Runs" / "cal_c4" / "knots.json"
KNOTS = json.loads(knots_path.read_text()) if knots_path.exists() else None


def finished_runs():
    for d in DIRS:
        for summary in sorted(d.glob("*/summary.json")):
            m = re.match(r"(WUS|WUK|NUK)_(\w+)_s(\d+)$", summary.parent.name)
            if m:
                yield summary.parent, m[1], CONDITIONS[m[2]], int(m[3])


def active_closure(closures, start, end):
    return np.array([((closures.start_s < e) & (closures.end_s > s)).any() for s, e in zip(start, end)], dtype=bool)


trip_frames, run_rows, sensing_rows, detection_rows = [], [], [], []
for run, mode, cond, seed in finished_runs():
    trips = pd.read_csv(run / "trips.csv")
    trips = trips[(trips.t_start >= WARMUP) & (trips.kind != "ward_from_corridor")].copy()
    closures = pd.read_csv(run / "schedule_closures.csv")
    trips["duration"] = trips.t_end - trips.t_start
    trips["during_closure"] = active_closure(closures, trips.t_start.to_numpy(), trips.t_end.to_numpy())
    trips["mode"], trips["cond"], trips["seed"] = mode, cond, seed
    trip_frames.append(trips)
    during = trips[trips.during_closure]
    stretcher = trips[trips.stretcher == 1]
    run_rows.append(dict(mode=mode, cond=cond, seed=seed, trips=len(trips), duration=trips.duration.mean(),
                         walked=trips.walked_m.mean(), blocked_per100=100 * trips.blocked.sum() / max(len(trips), 1),
                         trips_closure=len(during), duration_closure=during.duration.mean(), walked_closure=during.walked_m.mean(),
                         blocked_closure_per100=100 * during.blocked.sum() / max(len(during), 1),
                         duration_stretcher=stretcher.duration.mean(), critical_inbound=trips[(trips.critical == 1) & (trips.kind == "inbound")].duration.mean(),
                         stretcher_trips=len(stretcher), blocked_stretcher_per100=100 * stretcher.blocked.sum() / max(len(stretcher), 1),
                         make_way_per100=100 * stretcher.make_way.sum() / max(len(stretcher), 1),
                         walked_stretcher=stretcher.walked_m.mean()))

    if cond != "C3":
        continue
    cells = pd.read_csv(run / "cells.csv", usecols=["t", "cell", "true_density", "true_free_w", "sensed", "doppler_density",
                                                    "fused_density", "sensed_free_w"])
    cells = cells[cells.t >= WARMUP]
    sensed = cells[cells.sensed == 1]
    calibrated = np.interp(sensed.fused_density, KNOTS["calibrationX"], KNOTS["calibrationY"]) if KNOTS else np.full(len(sensed), np.nan)
    for name, est in (("doppler", sensed.doppler_density.to_numpy()), ("fused", sensed.fused_density.to_numpy()), ("calibrated", calibrated)):
        err = est - sensed.true_density.to_numpy()
        occupied = sensed.true_density.to_numpy() > 0
        sensing_rows.append(dict(mode=mode, seed=seed, estimate=name, mae=np.abs(err).mean(), bias=err.mean(),
                                 mae_occupied=np.abs(err[occupied]).mean(), bias_occupied=err[occupied].mean()))
    true_blocked = sensed.true_free_w < STRETCHER_WIDTH
    flagged = sensed.sensed_free_w < STRETCHER_WIDTH
    false_flags = (flagged & ~true_blocked).sum()
    for _, c in closures[(closures.start_s >= WARMUP) & (closures.end_s <= cells.t.max())].iterrows():
        rows = cells[(cells.cell == c.cell) & (cells.t >= c.start_s) & (cells.t <= c.end_s)]
        hit = rows[rows.sensed_free_w < STRETCHER_WIDTH]
        detection_rows.append(dict(mode=mode, seed=seed, cell=c.cell, start=c.start_s, duration=c.end_s - c.start_s,
                                   detected=len(hit) > 0, delay=hit.t.min() - c.start_s if len(hit) else np.nan,
                                   false_flag_share=false_flags / max((~true_blocked).sum(), 1)))

OUT.mkdir(parents=True, exist_ok=True)
runs = pd.DataFrame(run_rows).sort_values(["mode", "cond", "seed"])
runs.to_csv(OUT / "final_runs.csv", index=False)
print("finished runs by mode and condition:\n", runs.groupby(["mode", "cond"]).size().unstack().to_string(), "\n")

metrics = ["duration", "duration_closure", "walked_closure", "blocked_per100", "blocked_closure_per100", "duration_stretcher",
           "walked_stretcher", "blocked_stretcher_per100", "make_way_per100", "critical_inbound"]
paired = []
for mode, g in runs.groupby("mode"):
    base = g[g.cond == "C0"].set_index("seed")
    for cond, h in g.groupby("cond"):
        h = h.set_index("seed")
        seeds = base.index.intersection(h.index)
        for m in metrics:
            row = dict(mode=mode, cond=cond, metric=m, n=len(seeds), mean=h[m].mean())
            if cond != "C0" and len(seeds) >= 2:
                d = (h.loc[seeds, m] - base.loc[seeds, m]).dropna()
                half = stats.t.ppf(0.975, len(d) - 1) * d.std(ddof=1) / np.sqrt(len(d)) if len(d) > 1 else np.nan
                row.update(diff_vs_c0=d.mean(), ci_low=d.mean() - half, ci_high=d.mean() + half,
                           p=stats.ttest_rel(h.loc[d.index, m], base.loc[d.index, m]).pvalue if len(d) > 1 else np.nan)
            paired.append(row)
paired = pd.DataFrame(paired)
paired.to_csv(OUT / "final_paired.csv", index=False)
with pd.option_context("display.width", 200):
    for m in metrics:
        print(f"--- {m} (run means; difference vs C0 with 95% CI, paired by seed)")
        print(paired[paired.metric == m].drop(columns="metric").round(3).to_string(index=False), "\n")

if sensing_rows:
    sensing = pd.DataFrame(sensing_rows)
    sensing.to_csv(OUT / "final_sensing.csv", index=False)
    print("density estimates (C3 runs, sensed cell-seconds):")
    print(sensing.groupby(["mode", "estimate"])[["mae", "bias", "mae_occupied", "bias_occupied"]].mean().round(4).to_string(), "\n")
if detection_rows:
    detection = pd.DataFrame(detection_rows)
    detection.to_csv(OUT / "final_detection.csv", index=False)
    print("closure detection by the static channel (C3 runs):")
    print(detection.groupby("mode").agg(closures=("detected", "size"), detected=("detected", "mean"), delay_median=("delay", "median"),
                                        delay_p90=("delay", lambda x: x.quantile(0.9)), false_flags=("false_flag_share", "mean")).round(4).to_string())

fig, ax = plt.subplots(1, 4, figsize=(18, 4.2))
order = [c for c in ["C0", "C1", "C2", "C3", "C4"] if c in set(runs.cond)]
panels = [("duration_closure", "trip duration during closures (s)", ["WUS", "WUK"]),
          ("blocked_per100", "blocked encounters per 100 trips", ["WUS", "WUK", "NUK"]),
          ("duration_stretcher", "stretcher trip duration (s)", ["WUS", "WUK", "NUK"]),
          ("make_way_per100", "trolleys pushed aside\nper 100 stretcher trips", ["NUK"])]
for k, (m, label, modes) in enumerate(panels):
    for j, mode in enumerate(modes):
        g = runs[runs["mode"] == mode].groupby("cond")[m].agg(["mean", "std", "count"]).reindex(order)
        x = np.arange(len(order)) + (j - 1) * 0.25
        ax[k].errorbar(x, g["mean"], yerr=1.96 * g["std"] / np.sqrt(g["count"]), fmt="o", capsize=3, label=mode)
    ax[k].set_xticks(range(len(order)), order)
    ax[k].set_ylabel(label)
    ax[k].legend()
fig.tight_layout()
fig.savefig(OUT / "final_routing.png", dpi=150)
print("\nfigure:", OUT / "final_routing.png")
