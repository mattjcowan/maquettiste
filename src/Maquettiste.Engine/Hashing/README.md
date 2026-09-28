# Hashing

**Owner:** Scaffold. See docs/engineering/engine-design.md section 18.

`ContentHash` (SHA-256 lowercase hex: ETags, manifest and file hashes) and `HashBuilder` (`H(...)` over length-prefixed fields, section 11).

- Implements: complete.
- Consumes: nothing.

## Performance notes (WP, gate 1)

- `HashBuilder` gathers its length-prefixed fields in a pooled buffer and hashes them with one `SHA256.HashData` call on `Finish`
  (past 64 KiB it streams the buffer into an `IncrementalHash`). The bytes hashed are unchanged, so every `H(...)` value is the
  same as before; what changed is the cost of the short-field hashes the planner, skip check and loader compute by the hundred
  thousand (no native hash context per builder, no update call per field). `Hashing/HashingTests` checks the result against the
  length-prefixed SHA-256 definition, including null, multi-byte, over-64-KiB and many-field inputs.

