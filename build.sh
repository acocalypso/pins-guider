#!/usr/bin/env bash
# Builds and tests inside the official .NET SDK container (no host SDK required).
# Usage: ./build.sh [dotnet args...]   default: test
set -euo pipefail
cd "$(dirname "$0")"
args=("$@"); [ ${#args[@]} -eq 0 ] && args=(test PinsGuider.slnx)
exec docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e NUGET_PACKAGES=/nuget -v "${NUGET_CACHE:-$HOME/.nuget/packages}:/nuget" \
  -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet "${args[@]}"
