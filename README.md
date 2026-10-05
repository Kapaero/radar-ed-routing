# Corridor radar for stretcher routing in an overcrowded emergency department

Simulation code, configurations and aggregated results for the letter *"Corridor Radar for Stretcher Routing in Overcrowded Emergency Departments: Passability Matters More Than Crowd Density"*.

## Model

The model is an agent-based simulation of a 30-cubicle emergency department (ED) wing in Unity 6 (6000.3.2f1). It simulates:
- patients, companions, nurses, physicians and porters;
- aggressive incidents, corridor closures and trolleys parked in corridors (corridor care).

Each corridor cell has a millimetre-wave radar model with a Doppler (moving-target) channel and a static occupancy channel. A router chooses patient routes under five information conditions:

| Condition | What the router sees |
|---|---|
| C0 | nothing |
| C1 | true state (perfect sensor) |
| C2 | Doppler channel |
| C3 | Doppler + static channels |
| C4 | C3 with a calibrated density curve |

There is also an ablation, `OracleWidthOnly`, that sees the true free width but no density.

| Path | Content |
|---|---|
| `Assets/RadarCrowd/Scripts` | Simulation: agents, routing, radar model, experiment runner, metrics |
| `Assets/RadarCrowd/Scenes` | `Hospital.unity` (ED wing) and `JuelichCorridor.unity` (validation corridor) |
| `Assets/RadarCrowd/Editor` | Headless player builds, NavMesh baking |
| `Tools` | Input preparation (NHAMCS), calibration, Jülich validation, campaign scripts, analysis |
| `Data/derived/ed_inputs.json` | Empirical process inputs derived from NHAMCS 2016–2019 |
| `Runs/*/` | Configuration of every run reported in the letter (`*.json`) |
| `Docs/results`, `Docs/validation` | Aggregated results (CSV) and figures |

## Reproducing the results

1. Build the headless players in Unity (menu *RadarCrowd → Build player* and *Build validation player*), or run in batch mode:
   ```
   Unity -batchmode -quit -projectPath . -executeMethod RadarCrowd.EditorTools.HeadlessBuilder.Build -buildOutput Builds/RadarCrowdV6/RadarCrowd.exe
   ```
2. Run every configuration of a folder. The script passes `-job-worker-count 0`, which makes the runs deterministic: two runs with the same configuration produce byte-identical outputs.
   ```
   EXE=./Builds/RadarCrowdV6/RadarCrowd.exe Tools/run_configs.sh Runs/final 8
   ```
   The C4 runs need the calibration curve. `Tools/run_final_campaign.sh` runs `Runs/cal_c4` first, then fits the curve with `Tools/fit_calibration.py`, then creates and runs `Runs/final_c4`.
3. Analyse:
   - `python Tools/final_analysis.py` produces `Docs/results`;
   - `python Tools/review_analysis.py` covers the ablation and the sensitivity analysis.

Each 10-hour run takes about 10–20 minutes on one core. The full set of 240 runs writes about 13 GB of outputs, which are not included here.

## External data (not redistributed)

- **NHAMCS ED public-use files 2016–2019** (CDC/NCHS, https://www.cdc.gov/nchs/ahcd/). `Tools/make_ed_inputs.py` builds `Data/derived/ed_inputs.json` from them.
- **Jülich unidirectional corridor experiments 2009**, open boundary (DOI 10.34735/ped.2009.14). Place the `uo-*.txt` files in `Data/external/juelich/uo/`, then:
  - run `Tools/juelich_configs.py` to create the configurations;
  - run `Runs/val_juelich` with the validation player;
  - run `Tools/juelich_validation.py` to compare model and experiment (requires PedPy).

## Licence

MIT, see `LICENSE`.
