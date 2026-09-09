#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ -x "${TASK_PROJECT_ROOT}/.dotnet/dotnet" ]]; then
    readonly TASK_DOTNET="${TASK_PROJECT_ROOT}/.dotnet/dotnet"
elif [[ -x /tmp/tarkov-dotnet/dotnet ]]; then
    readonly TASK_DOTNET="/tmp/tarkov-dotnet/dotnet"
else
    readonly TASK_DOTNET="$(command -v dotnet)"
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_CLI_HOME="${TMPDIR:-/tmp}/tarkov-companion-dotnet-home"
export NUGET_PACKAGES="${TMPDIR:-/tmp}/tarkov-companion-nuget"

"${TASK_DOTNET}" build "${TASK_PROJECT_ROOT}/TarkovCompanion.sln" \
    --configuration Release \
    --no-restore \
    --disable-build-servers \
    --maxcpucount:1 \
    --verbosity minimal
