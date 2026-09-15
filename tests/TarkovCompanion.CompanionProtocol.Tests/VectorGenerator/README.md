# Paired-protocol vector generator

These Python standard-library scripts produce `../Golden` from `docs/PAIRED_DEVICE_PROTOCOL.md`
without using the C# library, so the C# tests compare two independent readings of the same byte
layouts. `primitives.py` implements P-256, ECDSA, HKDF, and AES-256-GCM and checks them against
RFC 5869, FIPS-197, and the GCM specification test vectors before any vector is written. The
keys are derived from public labels and are test-only.

- `gen_handshake.py <out>` writes `crypto/`, `handshake/`, and `relay/`.
- `gen_wire.py <out>` writes `commands/`, `client/`, `server/`, `hello/`, and `reconnect/`.
- `validate.py <schema> <golden>` validates every wire vector against the schema, including a
  strict check that no member is undeclared.

The outputs are deterministic; regenerating must leave `../Golden` unchanged unless the
normative document changed. Run them only on an approved development host, never on Clayton's
workstation; CI evidence comes from the C# tests.
