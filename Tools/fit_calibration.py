"""Fits the C4 calibration curve: monotone (isotonic) map from the radar's fused density estimate to the true cell
density, on calibration runs that use their own seeds. Prints the knots as JSON for the run configs and the error
of the raw and calibrated estimates.
Usage: python fit_calibration.py <warmup_seconds> <run_dir> [<run_dir> ...]"""
import json
import sys
from pathlib import Path

import numpy as np
import pandas as pd
from sklearn.isotonic import IsotonicRegression

warmup = float(sys.argv[1])
frames = []
for run in sys.argv[2:]:
    d = pd.read_csv(Path(run) / "cells.csv")
    frames.append(d[(d.t >= warmup) & (d.sensed == 1)])
d = pd.concat(frames, ignore_index=True)
x = d.fused_density.to_numpy(float)
y = d.true_density.to_numpy(float)

iso = IsotonicRegression(increasing=True, out_of_bounds="clip").fit(x, y)
levels = np.unique(np.concatenate([[0.0], np.quantile(x, np.linspace(0.5, 1.0, 11))]))
knots_x = [round(float(v), 4) for v in levels]
knots_y = [round(float(v), 4) for v in iso.predict(levels)]
calibrated = np.interp(x, knots_x, knots_y)

print(f"cell-seconds: {len(x)}, true density mean {y.mean():.3f}, max {y.max():.3f}")
print(f"MAE raw fused {np.abs(x - y).mean():.4f}, calibrated {np.abs(calibrated - y).mean():.4f}")
for lo, hi in [(0, 0.05), (0.05, 0.2), (0.2, 0.5), (0.5, 10)]:
    m = (y >= lo) & (y < hi)
    if m.any():
        print(f"  true in [{lo}, {hi}): n={m.sum()}, raw bias {np.mean(x[m] - y[m]):+.3f}, calibrated bias {np.mean(calibrated[m] - y[m]):+.3f}")
print(json.dumps({"calibrationX": knots_x, "calibrationY": knots_y}))
