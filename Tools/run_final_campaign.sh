#!/usr/bin/env bash
# Final campaign on the V5 player, run detached from any terminal session:
#   chain A: C4 calibration runs -> isotonic fit -> C4 runs;   chain B: C0-C3 runs.
# Progress and the end marker go to Runs/campaign.log.
cd "$(dirname "$0")/.."
PY=python
export EXE=./Builds/RadarCrowdV5/RadarCrowd.exe
log=Runs/campaign.log
echo "campaign started $(date '+%F %T')" > "$log"
(
  Tools/run_configs.sh Runs/cal_c4 6 >> "$log" 2>&1
  "$PY" Tools/fit_calibration.py 7200 $(ls -d Runs/cal_c4/*/) > Runs/cal_c4/fit.txt 2>&1
  tail -1 Runs/cal_c4/fit.txt > Runs/cal_c4/knots.json
  "$PY" Tools/make_final_configs.py final_c4 Runs/cal_c4/knots.json >> "$log" 2>&1
  Tools/run_configs.sh Runs/final_c4 6 >> "$log" 2>&1
  echo "chain A done $(date '+%F %T')" >> "$log"
) &
(
  Tools/run_configs.sh Runs/final 8 >> "$log" 2>&1
  echo "chain B done $(date '+%F %T')" >> "$log"
) &
wait
echo "campaign finished $(date '+%F %T')" >> "$log"
