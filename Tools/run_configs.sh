#!/usr/bin/env bash
# Runs every <name>.json config in a run directory with the headless player, at most $JOBS at a time.
# Output of each run goes to <dir>/<name>/, the player log to <dir>/<name>.log. Unity job worker threads are switched
# off (-job-worker-count 0): the NavMesh crowd update is not reproducible on worker threads.
# Usage: [EXE=path/to/player.exe] Tools/run_configs.sh Runs/<dir> [JOBS]   (default player: Builds/RadarCrowd/RadarCrowd.exe)
set -u
cd "$(dirname "$0")/.."
dir="$1"; jobs="${2:-4}"
root="$(pwd -W 2>/dev/null || pwd)"
start=$(date +%s)
for f in "$dir"/*.json; do
    name=$(basename "$f" .json)
    while [ "$(jobs -rp | wc -l)" -ge "$jobs" ]; do wait -n; done
    "${EXE:-./Builds/RadarCrowd/RadarCrowd.exe}" -batchmode -nographics -job-worker-count 0 -config "$root/$f" -out "$root/$dir/$name" -logFile "$root/$dir/$name.log" > /dev/null 2>&1 &
done
wait
echo "runs in $dir finished, wall $(( $(date +%s) - start )) s"
grep -a -l "Exception" "$dir"/*.log 2>/dev/null | sed 's/^/exception in /'
grep -a -c -H "can only be called on an active agent" "$dir"/*.log 2>/dev/null | grep -v ":0$" | sed 's/^/navmesh warnings: /'
exit 0
