#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly TASK_DOTNET_DIR="${TASK_PROJECT_ROOT}/.dotnet"
readonly TASK_SDK_VERSION="10.0.401"
readonly TASK_SDK_ARCHIVE="dotnet-sdk-${TASK_SDK_VERSION}-linux-x64.tar.gz"
readonly TASK_SDK_URL="https://builds.dotnet.microsoft.com/dotnet/Sdk/${TASK_SDK_VERSION}/${TASK_SDK_ARCHIVE}"
readonly TASK_SDK_SHA512="51c8b999af9e8dd9998c9edc5944e19a90788862068acd38694e098889054ce8c23d4f0c5cccfa16bf187d044562359e5ee69a9f8ad0bbe913ba90311fbce25b"

if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks | rg -q '^10\.0\.401 '; then
    exit 0
fi

if [[ -x "${TASK_DOTNET_DIR}/dotnet" ]]; then
    exit 0
fi

mkdir -p "${TASK_DOTNET_DIR}"
readonly TASK_ARCHIVE_PATH="${TMPDIR:-/tmp}/${TASK_SDK_ARCHIVE}"
curl -fL --retry 3 -o "${TASK_ARCHIVE_PATH}" "${TASK_SDK_URL}"
printf '%s  %s\n' "${TASK_SDK_SHA512}" "${TASK_ARCHIVE_PATH}" | sha512sum -c -
tar -xzf "${TASK_ARCHIVE_PATH}" -C "${TASK_DOTNET_DIR}"
"${TASK_DOTNET_DIR}/dotnet" --info
