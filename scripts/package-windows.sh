#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly TASK_PUBLISH_DIR="${TASK_PROJECT_ROOT}/artifacts/win-x64"
readonly TASK_PACKAGE="${TASK_PROJECT_ROOT}/dist/TarkovCompanion-v1.0.0-win-x64.zip"
readonly TASK_TEMP_PACKAGE="${TASK_PACKAGE%.zip}.tmp.zip"
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
export NUGET_PACKAGES="${NUGET_PACKAGES:-${TMPDIR:-/tmp}/tarkov-companion-nuget}"

if [[ "${TASK_PUBLISH_DIR}" != "${TASK_PROJECT_ROOT}/artifacts/win-x64" ]]; then
    printf 'Refusing to clean an unexpected staging directory: %s\n' "${TASK_PUBLISH_DIR}" >&2
    exit 1
fi

rm -rf -- "${TASK_PUBLISH_DIR}"
rm -f -- "${TASK_PACKAGE}" "${TASK_TEMP_PACKAGE}"
mkdir -p "${TASK_PUBLISH_DIR}" "${TASK_PROJECT_ROOT}/dist"

publish_project() {
    local project="$1"
    "${TASK_DOTNET}" publish "${project}" \
        --configuration Release \
        --runtime win-x64 \
        --self-contained true \
        --no-restore \
        --output "${TASK_PUBLISH_DIR}" \
        --disable-build-servers \
        --maxcpucount:1 \
        --verbosity minimal \
        -p:BuildInParallel=false \
        -p:PublishSingleFile=false \
        -p:DebugType=None \
        -p:DebugSymbols=false
}

publish_project "${TASK_APP_PROJECT}"
publish_project "${TASK_SIMULATOR_PROJECT}"

cp "${TASK_PROJECT_ROOT}/README.md" "${TASK_PUBLISH_DIR}/README.md"
cp "${TASK_PROJECT_ROOT}/LICENSE" "${TASK_PUBLISH_DIR}/LICENSE"
cp "${TASK_PROJECT_ROOT}/docs/LICENSING.md" "${TASK_PUBLISH_DIR}/LICENSING.md"
cp "${TASK_PROJECT_ROOT}/docs/THIRD_PARTY_NOTICES.md" "${TASK_PUBLISH_DIR}/THIRD_PARTY_NOTICES.md"
mkdir -p "${TASK_PUBLISH_DIR}/LICENSES"
cp -R "${TASK_PROJECT_ROOT}/LICENSES/." "${TASK_PUBLISH_DIR}/LICENSES/"
cp "${TASK_PROJECT_ROOT}/scripts/windows-smoke.ps1" "${TASK_PUBLISH_DIR}/windows-smoke.ps1"
"${TASK_PROJECT_ROOT}/scripts/audit-licenses.sh" \
    --output "${TASK_PUBLISH_DIR}/THIRD_PARTY_INVENTORY.json"

TASK_COMMIT="$(git -C "${TASK_PROJECT_ROOT}" rev-parse HEAD)"
TASK_BUILT_UTC="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
printf 'version=1.0.0\ncommit=%s\nbuilt_utc=%s\n' \
    "${TASK_COMMIT}" \
    "${TASK_BUILT_UTC}" \
    > "${TASK_PUBLISH_DIR}/BUILD_INFO.txt"

if [[ -e "${TASK_PUBLISH_DIR}/Data" || -e "${TASK_PUBLISH_DIR}/portable.flag" ]]; then
    printf 'Packaging failed: runtime data/cache content entered the clean staging directory\n' >&2
    exit 1
fi
if find "${TASK_PUBLISH_DIR}" -type f \
    \( -name 'tarkov-dev-maps.cache.json' -o -name '*.metadata.json' -o -name '*.preview.png' \) \
    -print -quit | grep -q '.'; then
    printf 'Packaging failed: runtime-cached tarkov.dev map content entered the staging directory\n' >&2
    exit 1
fi

if command -v zip >/dev/null 2>&1; then
    (
        cd "${TASK_PUBLISH_DIR}"
        zip -q -r "${TASK_TEMP_PACKAGE}" .
    )
elif command -v 7z >/dev/null 2>&1; then
    (
        cd "${TASK_PUBLISH_DIR}"
        7z a -bd -tzip "${TASK_TEMP_PACKAGE}" . >/dev/null
    )
else
    printf 'Packaging failed: neither zip nor 7z is available\n' >&2
    exit 1
fi

mv "${TASK_TEMP_PACKAGE}" "${TASK_PACKAGE}"
sha256sum "${TASK_PACKAGE}" > "${TASK_PROJECT_ROOT}/dist/SHA256SUMS.txt"

# The update manifest is written here, from the same values that went into
# BUILD_INFO.txt, rather than assembled later at publish time. Stamping it separately
# left the two disagreeing about when the same commit was built, by the three minutes
# between packaging and publishing. Harmless while only the commit is compared, and
# exactly the kind of skew that stays invisible until something compares the other field.
TASK_SHA256="$(awk '{print $1}' "${TASK_PROJECT_ROOT}/dist/SHA256SUMS.txt" | head -1)"
cat > "${TASK_PROJECT_ROOT}/dist/update.json" <<MANIFEST
{
  "version": "1.0.0",
  "commit": "${TASK_COMMIT}",
  "builtUtc": "${TASK_BUILT_UTC}",
  "asset": "$(basename "${TASK_PACKAGE}")",
  "sha256": "${TASK_SHA256}"
}
MANIFEST
printf 'Windows package created: %s\n' "${TASK_PACKAGE}"
