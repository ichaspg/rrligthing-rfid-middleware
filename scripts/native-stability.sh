#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
iterations="${1:-100}"
dotnet build tests/RfidMiddleware.NativeTests/RfidMiddleware.NativeTests.csproj -c Release
output="tests/RfidMiddleware.NativeTests/bin/Release/net8.0"
LD_LIBRARY_PATH="$output/native" valgrind --leak-check=full --show-leak-kinds=definite \
  --error-exitcode=99 dotnet "$output/RfidMiddleware.NativeTests.dll" "$iterations"
