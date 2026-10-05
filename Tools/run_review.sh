#!/usr/bin/env bash
cd "$(dirname "$0")/.."
export EXE=./Builds/RadarCrowdV6/RadarCrowd.exe
echo "review started $(date '+%F %T')" > Runs/review.log
Tools/run_configs.sh Runs/review 14 >> Runs/review.log 2>&1
echo "review finished $(date '+%F %T')" >> Runs/review.log
