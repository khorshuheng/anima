#!/usr/bin/env bash
# Run all Anima services locally. Ports are fixed by AnimaPorts.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

trap 'kill 0' EXIT INT TERM

dotnet run --project src/Anima.Knowledge.Service &
dotnet run --project src/Anima.Bus.Service &
dotnet run --project src/Anima.Automation.Service &
dotnet run --project src/Anima.Deploy.Service &
dotnet run --project src/Anima.Agent.Service &

wait
