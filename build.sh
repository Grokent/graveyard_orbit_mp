#!/usr/bin/env bash
# Build the co-op plugin with the project-local .NET toolchain (no system install).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

export DOTNET_ROOT="$ROOT/tools/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

dotnet build "$ROOT/src/GraveyardOrbit.Coop/GraveyardOrbit.Coop.csproj" -c Release "$@"
