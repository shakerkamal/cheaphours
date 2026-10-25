#!/bin/sh
# The simulator exits when it cannot reach the gateway or the connection drops. Restart it after a short
# pause, so the container stays up while the gateway is stopped and reconnects when it comes back.
while true; do
  npx tsx index_16.ts
  echo "Simulator exited with code $?; restarting in 3 s" >&2
  sleep 3
done
