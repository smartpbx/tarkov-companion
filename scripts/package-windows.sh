#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly TASK_PUBLISH_DIR="${TASK_PROJECT_ROOT}/artifacts/win-x64"
readonly TASK_PACKAGE="${TASK_PROJECT_ROOT}/dist/TarkovCompanion-v1.0.0-win-x64.zip"
readonly TASK_APP_PROJECT="${TASK_PROJECT_ROOT}/src/TarkovCompanion.App/TarkovCompanion.App.csproj"
readonly TASK_SIMULATOR_PROJECT="${TASK_PROJECT_ROOT}/src/TarkovCompanion.EftSimulator/TarkovCompanion.EftSimulator.csproj"
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

mkdir -p "${TASK_PUBLISH_DIR}" "${TASK_PROJECT_ROOT}/dist"
"${TASK_DOTNET}" publish "${TASK_APP_PROJECT}" \
    --configuration Release \
    --runtime win-x64 \
    --self-contained true \
    --output "${TASK_PUBLISH_DIR}" \
    -p:PublishSingleFile=false \
    -p:DebugType=None \
    -p:DebugSymbols=false
"${TASK_DOTNET}" publish "${TASK_SIMULATOR_PROJECT}" \
    --configuration Release \
    --runtime win-x64 \
    --self-contained true \
    --output "${TASK_PUBLISH_DIR}" \
    -p:PublishSingleFile=false \
    -p:DebugType=None \
    -p:DebugSymbols=false

cp "${TASK_PROJECT_ROOT}/README.md" "${TASK_PUBLISH_DIR}/README.md"
cp "${TASK_PROJECT_ROOT}/LICENSE" "${TASK_PUBLISH_DIR}/LICENSE"
cp "${TASK_PROJECT_ROOT}/docs/THIRD_PARTY_NOTICES.md" "${TASK_PUBLISH_DIR}/THIRD_PARTY_NOTICES.md"
cp "${TASK_PROJECT_ROOT}/scripts/windows-smoke.ps1" "${TASK_PUBLISH_DIR}/windows-smoke.ps1"
printf 'version=1.0.0\ncommit=%s\nbuilt_utc=%s\n' \
    "$(git -C "${TASK_PROJECT_ROOT}" rev-parse HEAD)" \
    "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    > "${TASK_PUBLISH_DIR}/BUILD_INFO.txt"
(
    cd "${TASK_PUBLISH_DIR}"
    zip -q -r "${TASK_PACKAGE}" .
)
sha256sum "${TASK_PACKAGE}" > "${TASK_PROJECT_ROOT}/dist/SHA256SUMS.txt"
