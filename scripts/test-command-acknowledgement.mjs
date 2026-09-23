// Node test for src/TarkovCompanion.GroupServer/Tablet/command-acknowledgement.js: what a person is
// told for every disposition the protocol can answer with, and the bookkeeping for commands that
// were never answered. Run with `node scripts/test-command-acknowledgement.mjs`; the unit-test
// suite runs it too (TabletAcknowledgementScriptTests), so a change here that breaks it fails CI.
import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const ack = (await import(pathToFileURL(path.join(here, "../src/TarkovCompanion.GroupServer/Tablet/command-acknowledgement.js")).href)).default;

let failures = 0;
function check(name, condition) {
  if (condition) {
    console.log(`ok - ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL - ${name}`);
  }
}

const DISPOSITIONS = [
  "Applied", "RejectedStale", "RejectedConflict", "UnsupportedVersion", "RejectedExpired",
  "RejectedUnauthorized", "RejectedInvalidState", "RequiresPreview", "RequiresSnapshot",
  "RejectedCommandIdReuse", "UnsupportedPreferenceSchema",
];

// Every disposition in CommandDisposition (WorkspaceState.cs) has an entry, so none can fall through silently.
for (const disposition of DISPOSITIONS) {
  check(`${disposition} is described`, Object.hasOwn(ack.OUTCOMES, disposition));
}
check("no disposition is described that the protocol does not have",
  Object.keys(ack.OUTCOMES).every((name) => DISPOSITIONS.includes(name)));

// Applied is the only thing that is not a message.
const applied = ack.describeAcknowledgement({ commandId: { value: "c1" }, disposition: "Applied", canonicalState: null });
check("an applied command says nothing", applied.applied === true && applied.message === null && applied.tone === "ok");
for (const disposition of DISPOSITIONS.filter((name) => name !== "Applied")) {
  const described = ack.describeAcknowledgement({ commandId: { value: "c2" }, disposition });
  check(`${disposition} is never read as applied and always says something`,
    described.applied === false && typeof described.message === "string" && described.message.length > 0
    && described.message.length <= 110);
}

// An answer this page cannot read is a refusal, not a success.
for (const odd of [{ disposition: "SomethingNew" }, { disposition: 3 }, {}, null, undefined]) {
  const described = ack.describeAcknowledgement(odd);
  check(`unreadable answer ${JSON.stringify(odd)} is a refusal`, described.applied === false && described.tone === "error" && !!described.message);
}
check("Object.prototype names are not dispositions",
  ack.describeAcknowledgement({ disposition: "toString" }).applied === false
  && ack.describeAcknowledgement({ disposition: "constructor" }).tone === "error");

// The state a stale or conflicting answer carries is handed back to be shown.
const state = { authorityEpoch: { value: "e" } };
check("a stale answer hands back the state to show",
  ack.describeAcknowledgement({ commandId: { value: "c3" }, disposition: "RejectedStale", canonicalState: state }).canonicalState === state);
check("the protocol's own code is never shown",
  !ack.describeAcknowledgement({ disposition: "RejectedStale", code: "stale-revision-42" }).message.includes("stale-revision"));

// [#601] A Control request refused for a capability the pairing's own grant lacks is an
// out-of-date pairing, not the desktop saying no, and only for the Control commands.
const denied = { commandId: { value: "c9" }, disposition: "RejectedUnauthorized", code: "capability-denied" };
for (const type of ["requestControl", "controlWorkspace"]) {
  const described = ack.describeAcknowledgement(denied, type);
  check(`${type} refused for its grant says pair again`,
    described.outOfDatePairing === true && described.message === "This pairing is out of date · pair again");
}
check("a mark refused for its grant is not called an out-of-date pairing",
  ack.describeAcknowledgement(denied, "upsertMark").outOfDatePairing === false);
check("Control refused for another reason is not called an out-of-date pairing",
  ack.describeAcknowledgement({ ...denied, code: "control-lease-required" }, "controlWorkspace").outOfDatePairing === false
  && ack.describeAcknowledgement({ ...denied, disposition: "RejectedStale" }, "requestControl").outOfDatePairing === false);
check("no command type means no out-of-date pairing", ack.describeAcknowledgement(denied).outOfDatePairing === false);

// Labels: what a person would call the thing they just did, for every command type index.html sends.
check("labels name what the person did",
  ack.labelFor({ type: "upsertMark", mark: { kind: "Ping" } }) === "Ping"
  && ack.labelFor({ type: "upsertMark", mark: { kind: "Waypoint" } }) === "Waypoint"
  && ack.labelFor({ type: "deleteMark" }) === "Removing a mark"
  && ack.labelFor({ type: "requestControl" }) === "Taking control"
  && ack.labelFor({ type: "setInteractionMode" }) === "Changing mode"
  && ack.labelFor({ type: "controlWorkspace" }) === "Moving the map"
  && ack.labelFor({ type: "controlWorkspace", action: { type: "navigate", workspace: "Plan" } }) === "Switching workspace"
  && ack.labelFor({ type: "requestCaptureIntent", intent: "Flea" }) === "Arming Flea capture"
  && ack.labelFor({ type: "showOnDesktop" }) === "Showing on the desktop"
  && ack.labelFor({ type: "somethingElse" }) === "That"
  && ack.labelFor(undefined) === "That");

// Pending commands.
const pending = new ack.PendingCommands(10_000, 3);
pending.track("a", "Ping", 1_000, "upsertMark");
pending.track("b", "Waypoint", 2_000);
check("a tracked command is pending", pending.count === 2);
const settled = pending.settle("a");
check("an answer settles the command it was for, and says what kind it was",
  settled?.label === "Ping" && settled?.type === "upsertMark" && pending.count === 1);
check("a second answer for the same command finds nothing", pending.settle("a") === null);
check("an answer for a command this page never sent finds nothing", pending.settle("zzz") === null);
check("nothing has waited too long yet", pending.expire(11_999).length === 0);
const late = pending.expire(12_000);
check("a command past the limit is reported once, with its label", late.length === 1 && late[0].label === "Waypoint");
check("and is not reported again", pending.expire(99_000).length === 0 && pending.count === 0);
pending.track("1", "x", 0); pending.track("2", "x", 0); pending.track("3", "x", 0); pending.track("4", "x", 0);
check("the list is bounded so a silent desktop cannot grow it", pending.count === 3 && pending.settle("1") === null);
pending.track("", "x", 0);
check("a command with no id is not tracked", pending.count === 3);

if (failures > 0) {
  console.error(`${failures} check(s) failed`);
  process.exit(1);
}

console.log("all checks passed");
