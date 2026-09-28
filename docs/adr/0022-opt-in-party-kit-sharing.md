# ADR 0022: Joining a relay room opts into party-visible kit sharing

## Status

Accepted on 2026-09-28.

## Context

Escape from Tarkov's group notifications are asymmetric. A player's game describes the equipment,
level, and side of the other people in their party, but does not give the companion the same
structured description of the local player. That makes a squadmate's observation the only
read-only source for showing a player their own current kit.

The version-1 protocol already carried those facts in the observed field. The relay pruned
observations to display names present in the keyed room and kept live member state only in memory,
but the safety policy still said third-party log data was never transmitted. A separate “share my
loadout” switch did not resolve that contradiction: the app cannot read its own carried loadout,
the switch always produced an empty own-loadout field, and it never controlled observed.

The product owner clarified the consent boundary: connecting to the relay is the opt-in for kit
sharing. The information is limited to what the squad already sees while assembling, and the
purpose is to return a player's own kit when their local log cannot.

## Decision

1. Group sharing remains off by default. Enabling it with a usable relay address, display name,
   and group key is explicit consent to exchange current party-visible kit, level, and side
   observations with that keyed room.
2. The client explains this next to the join control. Every holder of the room key, including a
   paired second screen, is within that consent boundary. Quest sharing remains separately
   optional.
3. The relay accepts an observation only when its nickname matches a display name already present
   in the same room, case-insensitively. It repeats that pruning on reads so a departed member's
   observation disappears immediately. A person who has not joined the room is never retained.
4. Observations are part of ephemeral member state: memory only, removed when the publisher
   leaves or expires, and never written to relay state files.
5. Shared observations contain named gear, level, and side only. Account/profile identifiers,
   health, dogtags, positions, raw item ids, and scav cooldowns are excluded. The legacy scav
   field remains parseable for wire compatibility but current clients do not send or consume it,
   and the relay clears it before storage or return.
6. The old own-loadout setting remains only as a settings/wire compatibility slot. It is no
   longer shown because the current app has no truthful source for the sender's own loadout;
   observed party data is governed by room membership, not by that dead switch.

## Consequences

- Two squad members running the companion can return each other's current kit/profile summary;
  one member alone sees no fabricated answer.
- Display-name matching is weaker than account identity. The UI states that matching is by
  in-game nickname, and the room key remains the access boundary until scoped relay identities
  replace it.
- A member observed before they join is dropped and appears after the observer's next publish.
  This deliberate one-tick delay keeps non-members out of relay memory.
- Older clients remain compatible, but a current relay removes their legacy scav-cooldown value.
- The former RISK-RELAY-OBSERVED-DATA-POLICY contradiction is resolved by an explicit, narrowly
  bounded consent exception. Broader relay credential and transport risks remain open.
