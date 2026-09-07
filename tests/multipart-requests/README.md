# Multipart request regression

Run from the Hawaii repository with a frozen tool assembly:

```sh
python3 tests/multipart-requests/run.py --assembly /absolute/path/to/Hawaii.dll
```

Without `--assembly`, the runner builds Hawaii first. This is a generator regression harness; the CloudEdge consumer's packaged-tool bootstrap remains separate.

The fixture is vendor-neutral and uses an object request composed with `allOf`, a named binary-file alias and list alias, inline metadata refinements, and a named metadata record. It also declares a raw JavaScript alternative so the regression detects an incorrect fallback.

The generated F# client sends requests to a real loopback HTTP listener. The listener parses the transmitted multipart body and verifies:

- Exact binary bytes, filenames and per-file media types.
- Repeated property names for file, scalar and structured arrays.
- Explicit `PartName` overrides without changing `FileName`.
- Effective-name collisions are rejected across owners, while repeated parts within an array remain valid.
- Failed construction leaves caller-owned request content intact.
- JSON metadata, inline refinements and additional JSON fields.
- Canonical named metadata accepted as its generated record type.
- Explicit text or binary values with branch-specific media and exact bytes.
- Dynamic field names, unchanged base64 strings, concrete part media, and declared charset encoding.
- Empty dynamic bodies and duplicate-name or wildcard-media refusal before HTTP transmission.
- A preserved-JSON metadata alias retains canonical identity and unknown fields.
- Omitted optional parts, an empty file list, and a required empty multipart body.
- Legacy `byte[] list` helper calls produce binary parts rather than JSON.

The runner also checks that an undeclared file media type fails before sending and that conflicting `allOf` wire types reject generation before output. Request bytes and command logs remain in the reported temporary work directory.

The inline refined metadata representation preserves JSON. It does not enforce field-level schema requirements, bounds or union membership.

Named binary aliases in this fixture exercise multipart transport classification. They do not assert that Hawaii's ordinary binary DTO alias mapping has been corrected; that separate limitation is recorded in [the multipart contract](../../docs/multipart-request-contract.md).
