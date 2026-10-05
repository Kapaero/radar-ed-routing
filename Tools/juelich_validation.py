"""Validation of the pedestrian model against the Jülich unidirectional corridor experiments (Düsseldorf 2009, open
boundary). Every run of the experiment and its NavMesh replay (Runs/val_juelich/<run>/sim_trajectories.txt) go through
the same analysis with PedPy, following Zhang et al. (2011, J. Stat. Mech. P06004): Voronoi density and Voronoi speed
in a measurement area 2 m long (y from -2 to 0 m) over the full corridor width, individual speeds from the displacement
over 10 frames at 16 fps (0.625 s), Voronoi cells bounded by the corridor walls (no cut-off). Only frames with at
least one pedestrian inside the measurement area are used. The simulated trajectories (25 fps) are resampled to 16 fps
by linear interpolation.
Output in Docs/validation: juelich_fd.png (fundamental diagrams), juelich_fd_bins.csv (mean speed by density bin),
juelich_runs.csv (mean density and speed of every run), juelich_frames.csv (all frame values).
Outputs of another run directory (e.g. Runs/val_juelich_pspeed) get its suffix (juelich_fd_pspeed.png, ...).
Usage: python juelich_validation.py [run_dir]"""
import re
import sys
from pathlib import Path

import matplotlib
import numpy as np
import pandas as pd
import pedpy
from pedpy import (MeasurementArea, SpeedCalculation, TrajectoryData, TrajectoryUnit, WalkableArea,
                   compute_individual_speed, compute_individual_voronoi_polygons, compute_voronoi_density,
                   compute_voronoi_speed, load_trajectory_from_txt)

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
RUNS = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "Runs" / "val_juelich"
EXP = ROOT / "Data" / "external" / "juelich" / "uo"
OUT = ROOT / "Docs" / "validation"
TAG = RUNS.name.replace("val_juelich", "") if RUNS.name.startswith("val_juelich") else "_" + RUNS.name
FPS = 16.0
FRAME_STEP = 5                 # +-5 frames: displacement over 10 frames = 0.625 s
CORRIDOR = (-4.0, 4.0)         # y range of the corridor (m)
MA_Y = (-2.0, 0.0)             # measurement area (m)
BIN = 0.25                     # density bin width (1/m^2)
V0, GAMMA, RHO_MAX = 1.34, 1.913, 5.4   # Weidmann (1993), as in the router


def weidmann(rho):
    rho = np.maximum(rho, 1e-6)
    return np.maximum(V0 * (1 - np.exp(-GAMMA * (1 / rho - 1 / RHO_MAX))), 0)


def sim_trajectory(path):
    """Simulated trajectories (cm, 25 fps) resampled to 16 fps, as PedPy trajectory data in metres."""
    header = path.read_text(encoding="utf-8").split("\n", 4)[:4]
    rate = float(next(h for h in header if "framerate" in h).split(":")[1])
    d = pd.read_csv(path, sep=r"\s+", comment="#", header=None, names=["id", "frame", "x", "y", "z"])
    parts = []
    for pid, g in d.groupby("id"):
        t = g.frame.to_numpy() / rate
        k = np.arange(np.ceil(t[0] * FPS), np.floor(t[-1] * FPS) + 1).astype(int)
        if len(k) == 0:
            continue
        parts.append(pd.DataFrame({"id": pid, "frame": k, "x": np.interp(k / FPS, t, g.x / 100), "y": np.interp(k / FPS, t, g.y / 100)}))
    return TrajectoryData(data=pd.concat(parts, ignore_index=True), frame_rate=FPS)


def fundamental_diagram(traj, b_cor):
    """Voronoi density and speed per frame in the measurement area (frames with someone inside it)."""
    walkable = WalkableArea([(0, CORRIDOR[0]), (b_cor, CORRIDOR[0]), (b_cor, CORRIDOR[1]), (0, CORRIDOR[1])])
    area = MeasurementArea([(0, MA_Y[0]), (b_cor, MA_Y[0]), (b_cor, MA_Y[1]), (0, MA_Y[1])])
    speed = compute_individual_speed(traj_data=traj, frame_step=FRAME_STEP, speed_calculation=SpeedCalculation.BORDER_SINGLE_SIDED)
    d = traj.data[(traj.data.y > CORRIDOR[0] + 1e-3) & (traj.data.y < CORRIDOR[1] - 1e-3)][["id", "frame", "x", "y"]].copy()
    d["x"] = d.x.clip(0.01, b_cor - 0.01)
    inside = TrajectoryData(data=d, frame_rate=traj.frame_rate)
    polygons = compute_individual_voronoi_polygons(traj_data=inside, walkable_area=walkable)
    density, intersecting = compute_voronoi_density(individual_voronoi_data=polygons, measurement_area=area)
    v = compute_voronoi_speed(traj_data=inside, individual_speed=speed, individual_voronoi_intersection=intersecting, measurement_area=area)
    occupied = d[(d.y >= MA_Y[0]) & (d.y <= MA_Y[1])].groupby("frame").size()
    density, v = (x.set_index("frame") if "frame" in x.columns else x for x in (density, v))
    fd = density[["density"]].join(v[["speed"]], how="inner")
    fd.index.name = "frame"
    fd = fd[fd.index.isin(occupied.index)].reset_index()
    fd["n_in_area"] = fd.frame.map(occupied)
    return fd[fd.density > 0]


rows, runs = [], []
for run_dir in sorted(p for p in RUNS.iterdir() if p.is_dir() and (p / "sim_trajectories.txt").exists()):
    m = re.match(r"uo-(\d{3})-(\d{3})-(\d{3})", run_dir.name)
    b_cor = int(m[2]) / 100
    exp = load_trajectory_from_txt(trajectory_file=EXP / f"{run_dir.name}.txt", default_frame_rate=FPS, default_unit=TrajectoryUnit.CENTIMETER)
    for source, traj in (("experiment", exp), ("simulation", sim_trajectory(run_dir / "sim_trajectories.txt"))):
        fd = fundamental_diagram(traj, b_cor)
        fd["run"], fd["source"], fd["b_cor"] = run_dir.name, source, b_cor
        rows.append(fd)
        runs.append(dict(run=run_dir.name, source=source, b_cor=b_cor, b_entrance=int(m[1]) / 100, b_exit=int(m[3]) / 100,
                         frames=len(fd), density=fd.density.mean(), speed=fd.speed.mean(),
                         flow_spec=(fd.density * fd.speed).mean(), peak_density=fd.density.quantile(0.95)))
    r = runs[-2:]
    print(f"{run_dir.name:20}: density exp {r[0]['density']:.2f} sim {r[1]['density']:.2f} | speed exp {r[0]['speed']:.2f} "
          f"sim {r[1]['speed']:.2f} | frames {r[0]['frames']}/{r[1]['frames']}", flush=True)

frames = pd.concat(rows, ignore_index=True)
per_run = pd.DataFrame(runs)
OUT.mkdir(parents=True, exist_ok=True)
frames.to_csv(OUT / f"juelich_frames{TAG}.csv", index=False)
per_run.to_csv(OUT / f"juelich_runs{TAG}.csv", index=False)

frames["bin"] = (frames.density // BIN) * BIN + BIN / 2
bins = (frames.groupby(["source", "bin"]).speed.agg(["mean", "std", "count"]).unstack("source"))
bins.columns = [f"{s}_{c}" for c, s in bins.columns]
bins["weidmann"] = weidmann(bins.index.to_numpy())
bins.to_csv(OUT / f"juelich_fd_bins{TAG}.csv")
both = bins.dropna(subset=["experiment_mean", "simulation_mean"])
both = both[(both.experiment_count >= 50) & (both.simulation_count >= 50)]
diff = both.simulation_mean - both.experiment_mean
print("\nmean speed by density bin (bins with >= 50 frames in both):")
print(both[["experiment_mean", "simulation_mean", "weidmann", "experiment_count", "simulation_count"]].round(3).to_string())
print(f"\nsimulation - experiment over {len(both)} bins: mean {diff.mean():+.3f} m/s, RMSE {np.sqrt((diff ** 2).mean()):.3f} m/s, "
      f"max |diff| {diff.abs().max():.3f} m/s")
wd = both.experiment_mean - both.weidmann
print(f"Weidmann - experiment: mean {-wd.mean():+.3f} m/s, RMSE {np.sqrt((wd ** 2).mean()):.3f} m/s")
pr = per_run.pivot(index="run", columns="source", values=["density", "speed"])
for q in ("density", "speed"):
    e, s = pr[q].experiment, pr[q].simulation
    print(f"per-run mean {q}: sim - exp {np.mean(s - e):+.3f} (RMSE {np.sqrt(np.mean((s - e) ** 2)):.3f}), correlation {np.corrcoef(e, s)[0, 1]:.3f}")

fig, ax = plt.subplots(1, 3, figsize=(15, 4.6))
colours = {"experiment": "0.25", "simulation": "tab:orange"}
for source, g in frames.groupby("source"):
    sample = g.sample(min(len(g), 6000), random_state=1)
    ax[0].scatter(sample.density, sample.speed, s=2, alpha=0.15, color=colours[source])
    ax[1].scatter(sample.density, sample.density * sample.speed, s=2, alpha=0.15, color=colours[source])
    b = bins[bins[f"{source}_count"] >= 50]
    ax[0].errorbar(b.index, b[f"{source}_mean"], yerr=b[f"{source}_std"], fmt="o-", ms=4, capsize=2, color=colours[source], label=source)
    ax[1].plot(b.index, b.index * b[f"{source}_mean"], "o-", ms=4, color=colours[source], label=source)
grid = np.linspace(0.05, max(4.0, frames.density.max()), 200)
ax[0].plot(grid, weidmann(grid), "--", color="tab:blue", label="Weidmann (router)")
ax[1].plot(grid, grid * weidmann(grid), "--", color="tab:blue", label="Weidmann (router)")
ax[0].set(xlabel="Voronoi density (1/m²)", ylabel="Voronoi speed (m/s)", ylim=(0, 2.2), title="Speed–density")
ax[1].set(xlabel="Voronoi density (1/m²)", ylabel="specific flow (1/(m·s))", title="Flow–density")
for q, marker in (("density", "o"), ("speed", "s")):
    ax[2].scatter(pr[q].experiment, pr[q].simulation, marker=marker, label=f"mean {q} per run")
lim = [0, max(pr.max().max(), 1.5) * 1.05]
ax[2].plot(lim, lim, "k:", lw=1)
ax[2].set(xlabel="experiment", ylabel="simulation", xlim=lim, ylim=lim, title="Per-run means (29 runs)")
for a in ax:
    a.legend(fontsize=8)
fig.tight_layout()
fig.savefig(OUT / f"juelich_fd{TAG}.png", dpi=150)
print("figure:", OUT / f"juelich_fd{TAG}.png")
