#!/usr/bin/env bash
set -euo pipefail

# Installs the one cosign every release signature is made and checked with, by content.
#
# The version is v3.1.3, the first v3 release with GHSA-fx35-mq7g-6g98 fixed: before it, a legacy
# bundle whose `cert` held a bare public key skipped certificate identity checks entirely, so
# `--certificate-identity` pinned nothing. A version tag is a name anybody who controls the
# release can move, and an installer action that downloads by tag checks whatever it was given.
# So the binary is fetched by version and accepted only if its sha256 is one committed in
# cosign.sha256. Those digests are the ones in cosign's own Sigstore-signed
# cosign_checksums.txt for v3.1.3, verified against the publisher identity
# keyless@projectsigstore.iam.gserviceaccount.com, and they equal GitHub's recorded asset digests.
if (($# != 1)); then
    printf 'Usage: scripts/release/install-cosign.sh DESTINATION-DIRECTORY\n' >&2
    exit 2
fi

TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly TASK_PROJECT_ROOT
readonly TASK_VERSION="v3.1.3"
readonly TASK_DESTINATION="$1"

case "$(uname -m)" in
    x86_64 | amd64) asset="cosign-linux-amd64" ;;
    aarch64 | arm64) asset="cosign-linux-arm64" ;;
    *) printf 'cosign install failed: no pinned cosign for %s\n' "$(uname -m)" >&2; exit 1 ;;
esac
readonly asset

expected="$(awk -v name="${asset}" '$2 == name {print $1}' "${TASK_PROJECT_ROOT}/scripts/release/cosign.sha256")"
[[ "${expected}" =~ ^[0-9a-f]{64}$ ]] || { printf 'cosign install failed: no pin for %s\n' "${asset}" >&2; exit 1; }

mkdir -p "${TASK_DESTINATION}"
temporary="$(mktemp "${TASK_DESTINATION}/cosign.XXXXXX")"
trap 'rm -f -- "${temporary}"' EXIT
url="https://github.com/sigstore/cosign/releases/download/${TASK_VERSION}/${asset}"
if command -v curl >/dev/null 2>&1; then
    curl --fail --silent --show-error --location --output "${temporary}" "${url}"
else
    wget -q -O "${temporary}" "${url}"
fi
actual="$(sha256sum "${temporary}" | awk '{print $1}')"
if [[ "${actual}" != "${expected}" ]]; then
    printf 'cosign install failed: %s has sha256 %s, not the pinned %s\n' "${asset}" "${actual}" "${expected}" >&2
    exit 1
fi
chmod 0755 "${temporary}"
mv -f -- "${temporary}" "${TASK_DESTINATION}/cosign"
trap - EXIT
printf 'Installed cosign %s (%s, sha256 %s) at %s\n' "${TASK_VERSION}" "${asset}" "${actual}" "${TASK_DESTINATION}/cosign"
