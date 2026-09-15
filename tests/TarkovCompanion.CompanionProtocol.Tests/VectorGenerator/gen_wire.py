"""Generates golden wire vectors for every non-handshake paired-protocol root."""
import copy, json, os, sys
OUT = sys.argv[1]

def gid(text): return {"value": text}
def rev(n): return {"value": n}
V = {"major": 2, "minor": 0}
EPOCH = gid("30000000-0000-0000-0000-000000000001")
DESKTOP = gid("10000000-0000-0000-0000-000000000001")
TABLET = gid("10000000-0000-4000-8000-000000000002")
DESKTOP_SESSION = gid("20000000-0000-4000-8000-000000000001")
SESSION = gid("20000000-0000-4000-8000-000000000002")
WORKSPACE = gid("80000000-0000-4000-8000-000000000001")
DESKTOP_INSTANCE = "desktop-install-1"
TABLET_INSTANCE = "tablet-install-1"
CONTRACT = {"major": 2, "minor": 0, "isDefined": True}
def origin(device, kind, instance): return {"workspaceId": WORKSPACE, "deviceId": device, "kind": kind, "instanceId": instance}
TABLET_ORIGIN = origin(TABLET, "PairedDevice", TABLET_INSTANCE)
DESKTOP_ORIGIN = origin(DESKTOP, "DesktopApplication", DESKTOP_INSTANCE)
def cmd(n): return gid("40000000-0000-4000-8000-%012d" % n)

T0 = "2026-09-14T20:00:00+00:00"
def at(seconds):
    minutes, secs = divmod(seconds, 60)
    hours, minutes = divmod(minutes, 60)
    return "2026-09-14T%02d:%02d:%02d+00:00" % (20 + hours, minutes, secs)

coordinate_world = {"mapId": "customs", "floorId": "ground", "coordinateSpace": "World", "projectionVersion": "tarkov-dev-1", "x": 10.5, "y": 2, "z": -20.25}
projection = {
    "workspace": "Raid", "mapId": "customs", "floorId": "ground",
    "viewport": {"center": coordinate_world, "zoom": 1.5},
    "selection": {"kind": "Objective", "referenceId": "objective-1", "originDeepLink": "tarkov-companion://objective/objective-1", "focusToken": "focus-1"},
    "visibleObjectiveIds": ["objective-1"], "planIds": [], "searchQuery": "labs keycard", "resultIds": ["item-1"],
    "activeLayers": ["extracts", "quests"], "activeFilters": ["found-in-raid"], "dialog": None,
}
device_modes = {
    "cursor": {"revision": rev(2), "lastChangeId": cmd(2)},
    "devices": [{"deviceId": TABLET, "mode": "Control", "changedUtc": at(20)}],
    "pendingControl": None,
    "controlLease": {"leaseId": gid("41000000-0000-4000-8000-000000000001"), "deviceId": TABLET, "sessionId": SESSION, "grantedUtc": at(20), "expiresUtc": at(140)},
}
workspace = {"cursor": {"revision": rev(3), "lastChangeId": cmd(3)}, "projection": projection}
# Marks carry the Core MapMarkState: map, floor, plane X/Y, label (at most 80 characters), and expiry.
marks = {"cursor": {"revision": rev(1), "lastChangeId": cmd(4)}, "marks": [{
    "markId": gid("50000000-0000-4000-8000-000000000001"), "revision": 1, "lastChangeId": cmd(4), "kind": "Waypoint", "scope": "PairedDevice",
    "authorDeviceId": TABLET,
    "state": {"mapId": "customs", "floorId": None, "x": 0.25, "y": 0.75, "label": "Stash spot", "expiresUtc": None},
    "coordinateSpace": "Normalized", "projectionVersion": "tarkov-dev-1", "height": None, "color": "#00AACC", "createdUtc": at(30), "updatedUtc": at(30)}]}
capture = {"cursor": {"revision": rev(1), "lastChangeId": cmd(5)}, "activeIntent": {
    "intentId": gid("60000000-0000-4000-8000-000000000001"), "correlationId": "capture-correlation-1",
    "captureSessionId": gid("70000000-0000-4000-8000-000000000001"),
    "state": {"intent": "Loot", "armedUtc": at(40), "expiresUtc": at(160)}, "initiatingDeviceId": TABLET,
    "initiatingSurface": "TabletLandscape", "status": "Armed",
    "context": {"mapId": "customs", "floorId": "ground", "profileId": "profile-1", "previousResultId": None, "objectiveIds": [], "planIds": [], "markIds": []},
    "progress": [{"sequence": 0, "phase": "Armed", "changedUtc": at(40), "percent": 0, "artifactId": None, "captureOrdinal": None, "detail": "recognition-intent-armed"}],
    "result": None, "guidance": [], "review": None, "corrections": []}}
preference_context = {
    "profileId": gid("81000000-0000-4000-8000-000000000001"), "generation": "wipe-generation-1", "mode": "Pvp",
    "wipeSeason": "2026-09", "language": "en-US", "region": "US", "timeZone": "Etc/UTC",
    "dataSnapshotId": "catalog-2026-09-14", "dataSnapshotPublishedUtc": "2026-09-13T20:00:00+00:00",
}
empty_preferences = {"context": preference_context, "schemaVersion": {"major": 1, "minor": 0}, "items": [], "protectedItemRules": [],
                     "recommendationOverrides": [], "favoriteLoadouts": [], "sharedPersonalization": []}
profile_preferences = {"cursor": {"revision": rev(0), "lastChangeId": None}, "activeProfile": None,
                       "lastChangedOrigin": None, "changedUtc": None}
updated_preferences = {"cursor": {"revision": rev(2), "lastChangeId": cmd(61)}, "activeProfile": {
    "context": preference_context, "schemaVersion": {"major": 1, "minor": 1},
    "items": [{"itemId": "item-gpu", "pinned": True, "wishlist": True, "sortOrder": 1}],
    "protectedItemRules": [{"ruleId": "rule-gpu", "selectorKind": "Item", "selectorId": "item-gpu", "disposition": "AlwaysKeep", "minimumQuantity": 3}],
    "recommendationOverrides": [{"itemId": "item-gpu", "action": "Prioritize", "reason": "Future quest"}],
    "favoriteLoadouts": [{"loadoutId": "loadout-1", "name": "Labs", "items": [{"slotId": "primary", "itemId": "item-rifle", "quantity": 1}]}],
    "sharedPersonalization": [{"kind": "Loadout", "referenceId": "loadout-1", "enabled": True}],
}, "lastChangedOrigin": TABLET_ORIGIN, "changedUtc": at(60)}
state = {"authorityEpoch": EPOCH, "workspaceId": WORKSPACE, "desktopInstanceId": DESKTOP_INSTANCE, "globalRevision": rev(7), "desktopDeviceId": DESKTOP,
         "deviceModes": device_modes, "workspace": workspace, "marks": marks, "captureIntent": capture, "profilePreferences": profile_preferences}

provenance = {"sourceClass": "GameWrittenScreenshot", "sourceIdentifier": "fixture://paired-capture", "observedUtc": at(60),
              "dataThroughUtc": None, "generatedUtc": None, "confidence": {"kind": "ProviderScore", "score": 0.9, "calibrationReference": None},
              "coverage": None, "producer": {"name": "paired-fixture", "version": "2.0", "modelVersion": None}, "reference": None, "inputs": [],
              "evidenceThroughUtc": at(60)}

def base(n, kind, issued=0, lifetime=60, preview=None, requested=1):
    return {"type": kind, "commandId": cmd(n), "requestedRevision": rev(requested), "issuedUtc": at(issued), "expiresUtc": at(issued + lifetime), "offlineQueuePreview": preview}

def envelope(command, session=SESSION):
    return {"protocolVersion": V, "sessionId": session, "authorityEpoch": EPOCH, "clientSentUtc": command["issuedUtc"], "command": command}

offline_preview = {"queuedUtc": at(0), "previewedUtc": at(600), "previewedAuthorityEpoch": EPOCH, "previewedAggregateRevision": rev(0)}
commands = {
    "set-interaction-mode": dict(base(1, "setInteractionMode"), mode="Independent"),
    "request-control": dict(base(2, "requestControl"), requestedLease="00:02:00"),
    "resolve-control": dict(base(3, "resolveControl"), requestCommandId=cmd(2), approved=True, leaseId=gid("41000000-0000-4000-8000-000000000001")),
    "preempt-control": dict(base(4, "preemptControl"), reason="desktop-user-resumed-control"),
    "update-desktop-workspace": dict(base(5, "updateDesktopWorkspace"), projection=projection),
    "control-workspace-navigate": dict(base(6, "controlWorkspace"), action={"type": "navigate", "workspace": "Plan", "mapId": "woods", "floorId": None, "viewport": None}),
    "control-workspace-search": dict(base(7, "controlWorkspace"), action={"type": "search", "query": "marked room"}),
    "control-workspace-filter": dict(base(8, "controlWorkspace"), action={"type": "filter", "activeLayers": ["extracts"], "activeFilters": []}),
    "control-workspace-select": dict(base(9, "controlWorkspace"), action={"type": "select", "selection": None}),
    "control-workspace-open-dialog": dict(base(10, "controlWorkspace"), action={"type": "openDialog", "dialog": None}),
    "show-on-desktop-offline": dict(base(11, "showOnDesktop", lifetime=900, preview=offline_preview), projection=projection),
    "upsert-mark": dict(base(12, "upsertMark"), markId=gid("50000000-0000-4000-8000-000000000002"), expectedMarkRevision=0,
                        mark={"kind": "Ping", "scope": "PairedDevice", "state": {"mapId": "customs", "floorId": "ground", "x": 10.5, "y": -20.25, "label": None, "expiresUtc": None},
                              "coordinateSpace": "World", "projectionVersion": "tarkov-dev-1", "height": 2, "color": "#FF8800"}),
    "delete-mark": dict(base(13, "deleteMark"), markId=gid("50000000-0000-4000-8000-000000000001"), expectedMarkRevision=1),
    "request-capture-intent": dict(base(14, "requestCaptureIntent"), intentId=gid("60000000-0000-4000-8000-000000000002"), correlationId="capture-correlation-2",
                                   captureSessionId=gid("70000000-0000-4000-8000-000000000002"), intent="Auto",
                                   context={"mapId": None, "floorId": None, "profileId": None, "previousResultId": None, "objectiveIds": [], "planIds": [], "markIds": [gid("50000000-0000-4000-8000-000000000001")]}),
    "report-capture-progress": dict(base(15, "reportCaptureProgress"), intentId=gid("60000000-0000-4000-8000-000000000001"), phase="Decoding", percent=30,
                                    artifactId="artifact-1", captureOrdinal=0, detail="decoding-visible-capture"),
    "publish-capture-result": dict(base(16, "publishCaptureResult", issued=60), intentId=gid("60000000-0000-4000-8000-000000000001"),
                                   result={"resultId": "result-1", "artifactId": "artifact-1", "captureOrdinal": 0,
                                           "status": {"completeness": "Complete", "freshness": "Current", "code": None, "detail": None},
                                           "detectedContext": "Loot", "completedUtc": at(60), "provenance": provenance},
                                   guidance=[{"kind": "ConfirmResult", "code": "review", "instruction": "Review the recognized items.", "order": 0}]),
    "review-capture-result": dict(base(17, "reviewCaptureResult"), intentId=gid("60000000-0000-4000-8000-000000000001"), disposition="NeedsCorrection", note="One item name is wrong."),
    "correct-capture-result": dict(base(18, "correctCaptureResult"), intentId=gid("60000000-0000-4000-8000-000000000001"), kind="ItemIdentity",
                                   fieldId="loot.items.0.canonicalId", correctedValue="item-corrected", reason="manual-review"),
    "activate-profile-preferences": dict(base(60, "activateProfilePreferences"), preferences=empty_preferences),
    "mutate-profile-preferences": dict(base(61, "mutateProfilePreferences", issued=60, requested=2), context=preference_context,
                                       schemaVersion={"major": 1, "minor": 0},
                                       mutation={"type": "setItem", "preference": {"itemId": "item-gpu", "pinned": True, "wishlist": True, "sortOrder": 1}}),
    "reset-profile-preferences": dict(base(62, "resetProfilePreferences", issued=120, requested=3), context=preference_context,
                                      schemaVersion={"major": 1, "minor": 0}),
    "delete-profile-preferences": dict(base(63, "deleteProfilePreferences", issued=180, requested=4), context=preference_context,
                                       schemaVersion={"major": 1, "minor": 1}),
}

def server(sequence, message, seconds=0, origin=TABLET):
    return {"protocolVersion": V, "sessionId": SESSION, "authenticatedOriginDeviceId": origin, "serverUtc": at(seconds), "deliverySequence": rev(sequence), "message": message}

applied_ack = {"type": "commandAcknowledgement", "acknowledgement": {"commandId": cmd(1), "aggregate": "DeviceModes", "requestedRevision": rev(1), "appliedRevision": rev(1),
    "appliedChangeId": cmd(1), "globalRevision": rev(1), "authorityEpoch": EPOCH, "disposition": "Applied", "code": "mode-applied", "canonicalState": None}}
conflict_ack = {"type": "commandAcknowledgement", "acknowledgement": {"commandId": cmd(99), "aggregate": "Workspace", "requestedRevision": rev(3), "appliedRevision": rev(3),
    "appliedChangeId": cmd(3), "globalRevision": rev(7), "authorityEpoch": EPOCH, "disposition": "RejectedConflict", "code": "revision-occupied-by-another-command", "canonicalState": state}}
# A rejection without canonical state describes no revision, so it never names this command or a reused id's original change.
reuse_ack = {"type": "commandAcknowledgement", "acknowledgement": {"commandId": cmd(4), "aggregate": "Marks", "requestedRevision": rev(2), "appliedRevision": rev(0),
    "appliedChangeId": None, "globalRevision": rev(7), "authorityEpoch": EPOCH, "disposition": "RejectedCommandIdReuse", "code": "command-id-reused", "canonicalState": None}}
unsupported_ack = {"type": "commandAcknowledgement", "acknowledgement": {"commandId": cmd(20), "aggregate": "Workspace", "requestedRevision": rev(4), "appliedRevision": rev(0),
    "appliedChangeId": None, "globalRevision": rev(7), "authorityEpoch": EPOCH, "disposition": "UnsupportedVersion", "code": "protocol-version-unreadable", "canonicalState": None}}
def update(kind, aggregate, global_revision, change, change_origin=TABLET_ORIGIN):
    return {"type": "canonicalUpdate", "update": {"type": kind, "authorityEpoch": EPOCH, "globalRevision": rev(global_revision), "changeId": change, "changedUtc": at(50),
                                                  "origin": change_origin, "contractVersion": CONTRACT, "state": aggregate}}
deprecation = {"type": "deprecation", "notice": {"deprecatedVersion": V, "sunsetUtc": "2027-03-01T00:00:00+00:00", "minimumReplacement": {"major": 2, "minor": 1},
    "recoveryAction": "UpdateTablet", "explanation": "Protocol 2.0 is replaced by 2.1; update the tablet before the sunset."}}

aggregate_acks = [
    {"aggregate": "DeviceModes", "revision": rev(2), "appliedChangeId": cmd(2), "acknowledgedUtc": at(70)},
    {"aggregate": "Workspace", "revision": rev(3), "appliedChangeId": cmd(3), "acknowledgedUtc": at(70)},
    {"aggregate": "Marks", "revision": rev(1), "appliedChangeId": cmd(4), "acknowledgedUtc": at(70)},
    {"aggregate": "CaptureIntent", "revision": rev(1), "appliedChangeId": cmd(5), "acknowledgedUtc": at(70)},
    {"aggregate": "ProfilePreferences", "revision": rev(0), "appliedChangeId": None, "acknowledgedUtc": at(70)},
]

files = {}
for name, command in commands.items():
    files["commands/%s.json" % name] = envelope(command, DESKTOP_SESSION if name == "activate-profile-preferences" else SESSION)
files["client/delivery-acknowledgement.json"] = {"protocolVersion": V, "sessionId": SESSION, "authorityEpoch": EPOCH, "throughDeliverySequence": rev(12),
                                                  "globalRevision": rev(7), "aggregateAcknowledgements": aggregate_acks}
files["server/command-acknowledgement-applied.json"] = server(1, applied_ack)
files["server/command-acknowledgement-conflict.json"] = server(2, conflict_ack)
files["server/command-acknowledgement-command-id-reuse.json"] = server(9, reuse_ack)
files["server/command-acknowledgement-unsupported-version.json"] = server(10, unsupported_ack)
files["server/canonical-snapshot.json"] = server(3, {"type": "canonicalSnapshot", "state": state}, origin=DESKTOP)
files["server/canonical-update-device-modes.json"] = server(4, update("deviceMode", device_modes, 2, cmd(2), DESKTOP_ORIGIN), origin=DESKTOP)
files["server/canonical-update-workspace.json"] = server(5, update("workspace", workspace, 5, cmd(3)))
files["server/canonical-update-marks.json"] = server(6, update("marks", marks, 6, cmd(4)))
files["server/canonical-update-capture-intent.json"] = server(7, update("captureIntent", capture, 7, cmd(5)))
files["server/canonical-update-profile-preferences.json"] = server(13, update("profilePreferences", updated_preferences, 8, cmd(61)), seconds=60)
files["server/deprecation.json"] = server(8, deprecation, origin=DESKTOP)
files["hello/client-hello.json"] = {"supportedVersions": {"minimum": V, "maximum": {"major": 2, "minor": 3}}, "clientInstanceId": "tablet-install-1", "optionalFeatures": ["independent-offline-queue"]}
files["hello/server-hello-compatible.json"] = {"disposition": "Compatible", "negotiatedVersion": V, "desktopVersions": {"minimum": V, "maximum": V}, "deprecation": None, "recoveryAction": None}
files["hello/server-hello-no-shared-major.json"] = {"disposition": "NoSharedMajor", "negotiatedVersion": None, "desktopVersions": {"minimum": V, "maximum": V}, "deprecation": None, "recoveryAction": "UpdateTablet"}
files["reconnect/reconnect-request.json"] = {"protocolVersion": V, "sessionId": SESSION, "authorityEpoch": EPOCH, "lastGlobalRevision": rev(7), "lastDeliverySequence": rev(9), "aggregateAcknowledgements": aggregate_acks}
files["reconnect/reconnect-request-without-cache.json"] = {"protocolVersion": V, "sessionId": SESSION, "authorityEpoch": None, "lastGlobalRevision": rev(0), "lastDeliverySequence": rev(0), "aggregateAcknowledgements": []}
def plan(disposition, replay, snapshot, resume, reason):
    return {"protocolVersion": V, "disposition": disposition, "replay": replay, "snapshot": snapshot, "resumeAfterDeliverySequence": rev(resume), "reason": reason}
files["reconnect/reconnect-plan-up-to-date.json"] = plan("UpToDate", [], None, 12, "already-current")
files["reconnect/reconnect-plan-replay.json"] = plan("Replay", [
    {"deliverySequence": rev(10), "serverUtc": at(50), "message": update("marks", marks, 6, cmd(4))},
    {"deliverySequence": rev(11), "serverUtc": at(51), "message": applied_ack}], None, 11, "bounded-replay")
files["reconnect/reconnect-plan-full-snapshot.json"] = plan("FullSnapshot", [], state, 12, "authority-or-cursor-mismatch")
files["reconnect/reconnect-plan-unsupported-version.json"] = plan("UnsupportedVersion", [], None, 9, "unsupported-version")

for relative, obj in files.items():
    path = os.path.join(OUT, relative)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as handle:
        json.dump(obj, handle, indent=2)
        handle.write("\n")
print(len(files), "wire vectors")
