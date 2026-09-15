# Profile fixtures

`profile-context-v1.json` is a synthetic, hand-specified profile-context transfer document holding no user data. It was exported by the version-1 `JsonProfileContextTransferCodec` as it stood at commit `100474e` and is committed byte for byte: 2970 bytes, one line, no byte-order mark, no trailing newline. `.gitattributes` marks it `-text` and `.editorconfig` turns off the final newline, so neither a checkout nor an editor rewrites the bytes the checksum was taken over.

The v1 checksum is SHA-256 of the codec's own re-serialization of the typed profile records, not of the received text. A change to record shape, member order, or a date, number, enum, or string representation can therefore change checksums for existing v1 documents. `ProfileTransferV2Tests.Committed_v1_fixture_decodes_to_exact_values_and_re_exports_byte_for_byte` catches changes that affect the values and record shapes represented by this fixture; other compatibility-sensitive values need additional fixtures.

Do not regenerate this file to make that test pass. A change that needs it regenerated is a format change: raise `FormatVersion`, decide explicitly whether v1 documents stay readable, and add a fixture for the new version beside this one.
