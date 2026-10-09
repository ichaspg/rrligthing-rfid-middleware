#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
duration_hours="${1:-2}"
deadline=$(( $(date +%s) + duration_hours * 3600 ))
mkdir -p "$root/artifacts/soak"
log="$root/artifacts/soak/metrics.csv"
echo "timestamp,rss_kb,threads,fds" > "$log"
while (( $(date +%s) < deadline )); do
  dotnet run --project "$root/tests/RfidMiddleware.LoadTests" -c Release --no-build -- 100000 1000000
  pid="${RFID_MIDDLEWARE_PID:-}"
  if [[ -n "$pid" && -d "/proc/$pid" ]]; then
    rss=$(awk '/VmRSS/{print $2}' "/proc/$pid/status")
    threads=$(awk '/Threads/{print $2}' "/proc/$pid/status")
    fds=$(find "/proc/$pid/fd" -mindepth 1 -maxdepth 1 | wc -l)
    echo "$(date --iso-8601=seconds),$rss,$threads,$fds" >> "$log"
  fi
done

