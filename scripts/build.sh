#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
mkdir -p artifacts
dotnet restore RfidMiddleware.slnx
dotnet build RfidMiddleware.slnx -c Release --no-restore
dotnet test RfidMiddleware.slnx -c Release --no-build
gcc -Wall -Wextra -Werror tests/native/abi_probe.c -o artifacts/abi_probe
artifacts/abi_probe
dotnet run --project tests/RfidMiddleware.LoadTests -c Release --no-build -- 100000 250000
