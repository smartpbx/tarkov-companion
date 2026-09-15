#!/usr/bin/env bash
set -euo pipefail

# Installs the Syft that generates the release SBOM, by content.
#
# The SBOM action this replaced installed Syft by running a remote install.sh for a version tag:
# a script and a tag, both fetched at run time, both whatever the release said they were that
# minute. This fetches the release tarball for 1.51.1 and accepts it only if its sha256 is one
# committed in syft.sha256. Those are the digests in Syft's own syft_1.51.1_checksums.txt, and
# they equal GitHub's recorded asset digests.
if (($# != 1)); then
    printf 'Usage: scripts/release/install-syft.sh DESTINATION-DIRECTORY\n' >&2
    exit 2
fi

TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly TASK_PROJECT_ROOT
readonly TASK_VERSION="1.51.1"
readonly TASK_DESTINATION="$1"

case "$(uname -m)" in
    x86_64 | amd64) asset="syft_${TASK_VERSION}_linux_amd64.tar.gz" ;;
    aarch64 | arm64) asset="syft_${TASK_VERSION}_linux_arm64.tar.gz" ;;
    *) printf 'syft install failed: no pinned syft for %s\n' "$(uname -m)" >&2; exit 1 ;;
esac
readonly asset

expected="$(awk -v name="${asset}" '$2 == name {print $1}' "${TASK_PROJECT_ROOT}/scripts/release/syft.sha256")"
[[ "${expected}" =~ ^[0-9a-f]{64}$ ]] || { printf 'syft install failed: no pin for %s\n' "${asset}" >&2; exit 1; }

work="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/syft.XXXXXX")"
trap 'rm -rf -- "${work}"' EXIT
curl --fail --silent --show-error --location --output "${work}/${asset}" \
    "https://github.com/anchore/syft/releases/download/v${TASK_VERSION}/${asset}"
actual="$(sha256sum "${work}/${asset}" | awk '{print $1}')"
if [[ "${actual}" != "${expected}" ]]; then
    printf 'syft install failed: %s has sha256 %s, not the pinned %s\n' "${asset}" "${actual}" "${expected}" >&2
    exit 1
fi
tar -xzf "${work}/${asset}" -C "${work}" syft
mkdir -p "${TASK_DESTINATION}"
install -m 0755 "${work}/syft" "${TASK_DESTINATION}/syft"
reported="$(SYFT_CHECK_FOR_APP_UPDATE=false "${TASK_DESTINATION}/syft" version | awk '/^Version:/ {print $2}')"
[[ "${reported}" == "${TASK_VERSION}" ]] || { printf 'syft install failed: the binary reports %s\n' "${reported}" >&2; exit 1; }
printf 'Installed syft %s (%s, sha256 %s) at %s\n' "${TASK_VERSION}" "${asset}" "${actual}" "${TASK_DESTINATION}/syft"
