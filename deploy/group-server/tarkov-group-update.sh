#!/usr/bin/env bash
#
# Updates the group relay to the newest verified build, unattended.
#
# The relay has no interface. Nobody can log into it and press a button, so without this it
# falls behind the desktop client, and the two have to speak the same protocol: the day the
# group key replaced a room and a server-side secret, a client that had updated could not talk
# to a server that had not.
#
# Everything here is arranged so that a failure leaves the service running the build it was
# already running. It takes a rollback copy before touching anything, verifies the download
# against a published checksum before unpacking it, and puts the old build back if the new one
# does not answer.
set -euo pipefail

readonly REPO="smartpbx/tarkov-companion"
readonly RELEASE="dev"
readonly ASSET="TarkovCompanion-GroupServer-linux-x64.tar.gz"
readonly SUMS="GROUPSERVER-SHA256SUMS.txt"
readonly INSTALL="/opt/tarkov-group"
readonly PREVIOUS="/opt/tarkov-group.previous"
# Outside the tree, because the rollback replaces the tree.
#
# The stamp used to live at ${INSTALL}/INSTALLED_SHA256, and the rollback is
# `mv "${PREVIOUS}" "${INSTALL}"` -- which restores the OLD stamp along with the old build. So
# a build that failed its health check was refused, rolled back, and then looked brand new to
# the next tick: fetched, swapped and rolled back again, every thirty minutes, for ever.
readonly STATE="/var/lib/tarkov-group"
readonly STAMP="${STATE}/INSTALLED_SHA256"
readonly REFUSED="${STATE}/REFUSED_SHA256"
# Written by the relay's admin panel and watched by tarkov-group-update.path. The relay runs
# unprivileged and cannot start a unit; it can write one file in the directory it already owns.
readonly REQUEST="${STATE}/UPDATE_NOW"
readonly SERVICE="tarkov-group"
readonly BASE="https://github.com/${REPO}/releases/download/${RELEASE}"

log() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"; }

work="$(mktemp -d)"
trap 'rm -rf "${work}"' EXIT

# wget rather than curl. The container does not have curl and cannot easily be given one,
# which the runbook already recorded and this script originally ignored. wget is present on a
# minimal Debian by default, which is the point of using it.
fetch() { wget -q --timeout=60 --tries=3 -O "$2" "$1"; }

# Removed first, before anything can fail or exit early. Left in place it would retrigger the
# path unit the instant this run finished, which for an up-to-date relay is a loop that asks
# GitHub for a checksum for ever.
rm -f "${REQUEST}"

log "checking ${REPO} ${RELEASE}"
fetch "${BASE}/${SUMS}" "${work}/${SUMS}"
expected="$(awk '{print $1}' "${work}/${SUMS}" | head -1)"
if [[ -z "${expected}" ]]; then
    log "no checksum published; refusing to update"
    exit 1
fi

# The stamp is what makes this idempotent. Without it the service would be restarted every
# time the timer fired, which for a relay means every member disappearing and coming back.
mkdir -p "${STATE}"

# Migration: the stamp used to live inside the tree. Move it once rather than treating an
# already-installed build as new and restarting the relay for nothing.
if [[ ! -f "${STAMP}" ]] && [[ -f "${INSTALL}/INSTALLED_SHA256" ]]; then
    mv "${INSTALL}/INSTALLED_SHA256" "${STAMP}"
    log "moved the install stamp out of the tree"
fi

if [[ -f "${STAMP}" ]] && [[ "$(cat "${STAMP}")" == "${expected}" ]]; then
    log "already on ${expected:0:12}; nothing to do"
    exit 0
fi

# A build this machine has already tried and rolled back is not tried again. Publishing a new
# one clears it, because the checksum will differ.
if [[ -f "${REFUSED}" ]] && [[ "$(cat "${REFUSED}")" == "${expected}" ]]; then
    log "${expected:0:12} was refused here before; not retrying until a new build is published"
    exit 0
fi

log "fetching ${ASSET}"
fetch "${BASE}/${ASSET}" "${work}/${ASSET}"
actual="$(sha256sum "${work}/${ASSET}" | awk '{print $1}')"
if [[ "${actual}" != "${expected}" ]]; then
    log "checksum mismatch: expected ${expected}, got ${actual}. Refusing."
    exit 1
fi
log "checksum verified ${actual:0:12}"

mkdir -p "${work}/new"
tar -C "${work}/new" -xzf "${work}/${ASSET}"
if [[ ! -f "${work}/new/TarkovCompanion.GroupServer" ]]; then
    log "the archive has no server in it; refusing"
    exit 1
fi

# The rollback copy is made before anything changes, so it exists throughout.
rm -rf "${PREVIOUS}"
cp -a "${INSTALL}" "${PREVIOUS}"

log "swapping"
systemctl stop "${SERVICE}"
rm -rf "${INSTALL}"
mv "${work}/new" "${INSTALL}"
chmod +x "${INSTALL}/TarkovCompanion.GroupServer"
printf '%s' "${expected}" > "${STAMP}"
systemctl start "${SERVICE}"

# Answering is the test, not starting. A process that starts and then fails to serve is the
# failure this is guarding against, and systemd calls that success.
for _ in $(seq 1 10); do
    sleep 2
    if wget -q --timeout=5 -O /dev/null http://127.0.0.1:8090/health 2>/dev/null; then
        log "updated to ${expected:0:12} and answering"
        exit 0
    fi
done

log "the new build did not answer; rolling back"
# Recorded before the rollback, and outside the tree the rollback replaces, so the refusal
# survives the thing that undoes the install.
printf '%s' "${expected}" > "${REFUSED}"
systemctl stop "${SERVICE}" || true
rm -rf "${INSTALL}"
mv "${PREVIOUS}" "${INSTALL}"
systemctl start "${SERVICE}"
log "rolled back; ${expected:0:12} will not be retried until a new build is published"
exit 1
