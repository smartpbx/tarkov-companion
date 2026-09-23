// What the desktop said about a command this tablet sent, in words a person can act on, and the
// list of commands still waiting for an answer.
//
// Before this, the page sent a command and never looked at the reply: a `commandAcknowledgement`
// message fell through `applyServerMessage` untouched, so a mark the desktop refused simply did
// not appear and nothing said why, and a command sent while the desktop was away looked exactly
// like one that worked (#290). A separate file, like relay-crypto.js, so it is plain JavaScript
// that Node can `require` and test (scripts/test-command-acknowledgement.mjs) instead of text
// inside a page nobody can run a unit test against.
(function (root, factory) {
  const exported = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = exported;
  } else {
    root.CommandAcknowledgement = exported;
  }
})(typeof self !== "undefined" ? self : globalThis, () => {
  "use strict";

  // Short and plain. The protocol's own `code` is a developer's word and is deliberately not shown.
  const OUTCOMES = {
    Applied: { tone: "ok", message: null },
    RejectedStale: { tone: "warn", message: "The desktop had already moved on, so that was not applied. Showing where it is now." },
    RejectedConflict: { tone: "warn", message: "Someone changed that first, so yours was not applied. Showing the current version." },
    RequiresPreview: { tone: "warn", message: "The desktop wants to look at that before it applies it." },
    RequiresSnapshot: { tone: "warn", message: "This tablet had fallen behind. It has caught up; try that again." },
    RejectedExpired: { tone: "error", message: "That took too long to arrive and expired. Try it again." },
    RejectedUnauthorized: { tone: "error", message: "The desktop did not allow that. It may have taken control back." },
    RejectedInvalidState: { tone: "error", message: "The desktop could not do that right now." },
    RejectedCommandIdReuse: { tone: "error", message: "That was already sent. Nothing was repeated." },
    UnsupportedVersion: { tone: "error", message: "This page and the desktop are out of step. Reload the page." },
    UnsupportedPreferenceSchema: { tone: "error", message: "This page and the desktop are out of step. Reload the page." },
  };
  const UNKNOWN = { tone: "error", message: "The desktop did not apply that." };
  const OUT_OF_DATE_PAIRING = { tone: "error", message: "This pairing is out of date · pair again" };
  const CONTROL_COMMAND_TYPES = new Set(["requestControl", "controlWorkspace"]);

  // A message this page cannot read yet is still a message: the protocol says an unrecognised
  // disposition is a refusal, never a success.
  //
  // [#601] `commandType` is what this page sent. A Control request refused because the pairing's
  // own grant lacks the capability ("capability-denied") is not the desktop saying no: it is a
  // pairing made by an older build, and no amount of waiting or retrying fixes it. Pairing again
  // does. The code decides the message; it is still never shown.
  function describeAcknowledgement(acknowledgement, commandType = null) {
    const commandId = acknowledgement?.commandId?.value ?? null;
    const disposition = typeof acknowledgement?.disposition === "string" ? acknowledgement.disposition : null;
    const outOfDatePairing = disposition === "RejectedUnauthorized" &&
      acknowledgement?.code === "capability-denied" &&
      CONTROL_COMMAND_TYPES.has(commandType);
    const outcome = outOfDatePairing
      ? OUT_OF_DATE_PAIRING
      : (disposition && Object.hasOwn(OUTCOMES, disposition) ? OUTCOMES[disposition] : null) ?? UNKNOWN;
    return {
      commandId,
      disposition,
      applied: disposition === "Applied",
      outOfDatePairing,
      tone: outcome.tone,
      message: outcome.message,
      // Stale, conflict, preview and snapshot answers carry the state to show instead. Applied never does.
      canonicalState: acknowledgement?.canonicalState ?? null,
    };
  }

  // What a person would call the thing they just did.
  function labelFor(command) {
    switch (command?.type) {
      case "upsertMark": return command.mark?.kind === "Ping" ? "Ping" : "Waypoint";
      case "deleteMark": return "Removing a mark";
      case "requestControl": return "Taking control";
      case "setInteractionMode": return "Changing mode";
      case "controlWorkspace": return command.action?.type === "navigate" && command.action?.workspace !== "Raid"
        ? "Switching workspace"
        : "Moving the map";
      case "requestCaptureIntent": return `Arming ${command.intent ?? "desktop"} capture`;
      case "showOnDesktop": return "Showing on the desktop";
      default: return "That";
    }
  }

  // Commands sent and not yet answered. Bounded, so a desktop that never answers cannot grow it.
  class PendingCommands {
    constructor(waitMs, maximum = 64) {
      this.waitMs = waitMs;
      this.maximum = maximum;
      this.entries = new Map();
    }

    get count() {
      return this.entries.size;
    }

    track(commandId, label, nowMs, type = null) {
      if (!commandId) return;
      this.entries.set(commandId, { commandId, label, type, sentMs: nowMs });
      while (this.entries.size > this.maximum) {
        this.entries.delete(this.entries.keys().next().value);
      }
    }

    // The entry an answer was for, or null when this page never sent it (or already gave up on it).
    settle(commandId) {
      const entry = this.entries.get(commandId) ?? null;
      this.entries.delete(commandId);
      return entry;
    }

    // Everything that has waited past the limit, removed so it is reported once.
    expire(nowMs) {
      const late = [];
      for (const entry of this.entries.values()) {
        if (nowMs - entry.sentMs >= this.waitMs) late.push(entry);
      }

      for (const entry of late) this.entries.delete(entry.commandId);
      return late;
    }

    clear() {
      this.entries.clear();
    }
  }

  return { OUTCOMES, describeAcknowledgement, labelFor, PendingCommands };
});
