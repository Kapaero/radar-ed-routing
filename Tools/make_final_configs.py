"""Configs of the final experiment and of the C4 calibration runs.

Fixed (calibrated or sourced) settings, shared by every run:
- arrivals: 2000 visits per treatment space per year (fits the NHAMCS time to first physician, KS 0.15-0.17);
- physicians: 5 patients per physician-hour (time to first physician); nurses 1:3 (NSW);
- errands per available hour: nurses 6.4, physicians 25, porters 8.5 (pedometer distances: nurses 0.55 vs 0.544 km/h,
  porters 0.81 vs 0.803 km/h; physicians reach 0.41 vs 0.515 km/h, being busy at the bedside most of the time);
- aggressive incidents: 9.25 per 1000 visits (midpoint of the reported 5.5-13);
- 10 h per run (06:00-16:00), the first 2 h are warm-up.

Scenarios:
- WUS: corridors as built (3.4-4.8 m), US operating mode (NHAMCS boarding times), random corridor closures
  (exponential gaps with mean 60 min, durations uniform 10-30 min);
- WUK: corridors as built, UK exit block (admitted patients wait for a ward bed on a corridor trolley, boarding times x3:
  18% of patients present on corridor trolleys, RCEM 13.5-19%), random corridor closures;
- NUK: corridors lined to the minimum clear width for bed movement (2.44 m, NFPA 101 / IBC 1020.2), UK exit block,
  no artificial closures: trolleys parked on both walls block stretchers; staff push a trolley aside in 120 s when a
  stretcher cannot pass otherwise.

Usage:
  python make_final_configs.py calibration            -> Runs/cal_c4 (DopplerStatic, seeds 31-33, every scenario)
  python make_final_configs.py final                  -> Runs/final (C0-C3, seeds 1-10, every scenario)
  python make_final_configs.py final_c4 knots.json    -> Runs/final_c4 (C4 with the fitted calibration knots)"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE = {
    "durationMin": 600, "warmupMin": 120, "startHour": 6,
    "edInputsPath": (ROOT / "Data" / "derived" / "ed_inputs.json").as_posix(),
    "visitsPerSpacePerYear": 2000, "patientsPerPhysicianHour": 5,
    "nurseErrandsPerHour": 6.4, "physicianErrandsPerHour": 25, "porterErrandsPerHour": 8.5,
    "incidentsPer1000": 9.25,
    # candidate routes: every corridor-graph route up to 2.5x the shortest (loop detours), at most 6 per cubicle
    "maxDetourRatio": 2.5, "maxRoutes": 6,
}
CLOSURES = {"closureGapMin": 60, "closureMinMin": 10, "closureMaxMin": 30}
UK = {"boardInCorridor": True, "boardingScale": 3}
SCENARIOS = {
    "WUS": {**CLOSURES},
    "WUK": {**CLOSURES, **UK},
    "NUK": {**UK, "corridorClearWidth": 2.44, "makeWaySec": 120},
}
CONDITIONS = ["NoSensing", "Oracle", "DopplerOnly", "DopplerStatic"]


def write(directory, run_id, seed, condition, scenario, extra=None):
    cfg = {"runId": run_id, "seed": seed, "condition": condition, **BASE, **SCENARIOS[scenario], **(extra or {})}
    (directory / f"{run_id}.json").write_text(json.dumps(cfg))


kind = sys.argv[1]
if kind == "calibration":
    out = ROOT / "Runs" / "cal_c4"
    out.mkdir(parents=True, exist_ok=True)
    for scenario in SCENARIOS:
        for seed in (31, 32, 33):
            write(out, f"{scenario}_cal_s{seed}", seed, "DopplerStatic", scenario)
else:
    out = ROOT / "Runs" / kind
    out.mkdir(parents=True, exist_ok=True)
    conditions, knots = (["Calibrated"], json.loads(Path(sys.argv[2]).read_text())) if kind == "final_c4" else (CONDITIONS, None)
    for scenario in SCENARIOS:
        for condition in conditions:
            for seed in range(1, 11):
                write(out, f"{scenario}_{condition}_s{seed:02d}", seed, condition, scenario, knots)
print(f"configs in {out}: {len(list(out.glob('*.json')))}")
