# FSharp.CloudEdge support

This work starts from Onur Gumus's `modernization` commit
`7899d3504ec2fa2e07e2790e02ef5829316cc1e6` (the source of
`Hawaii.Unofficial` 1.0.0). The local `fsharp-cloudedge-support` branch carries
generic generator corrections. Cloudflare schema pins, service organization,
and documented source overlays belong in the consuming FSharp.CloudEdge repo.

The response contract changes preserve exact HTTP statuses, OpenAPI status
classes (`1XX` through `5XX`), and an explicitly declared `default`. Exact
statuses take precedence over classes. Class/default union cases carry the
actual numeric status. Undeclared statuses fail explicitly; the final declared
response is no longer treated as an implicit default. Typed JSON strings are
deserialized, including error responses on binary endpoints.

Generated .NET task clients use FSharp.Core's task builder. The previous Ply
builder failed on ordinary HTTP calls in Debug builds. Microsoft.OpenApi and
its YAML reader are pinned to 3.5.4, which fixes the upstream
[circular-reference parser vulnerability](https://github.com/microsoft/OpenAPI.NET/security/advisories/GHSA-v5pm-xwqc-g5wc).

Schema handling rejects unresolved references before writing source. Operation
tags are allowed without a top-level tag declaration, as OpenAPI permits.
Single-member composition simplification preserves conflicting sibling
constraints. Named references and recursive inline records retain their
declaration identity; required fields inherited through `allOf` remain
required. Deprecated fields remain available to callers.

Named unions with disjoint JSON kinds or required, disjoint literal tags get
wire-dispatch converters. Their member records keep their own tag fields,
including when used independently. `allOf` tag constraints are intersected;
a discriminator mapping alone does not establish exclusivity. These converters
select and serialize a branch; they are not complete JSON Schema validators.

Unresolved named unions and `allOf` intersections containing union operands fail
generation by default. A .NET consumer can list
exact source schema IDs in `preserveJsonSchemas` to request distinct
`JsonElement`-backed wrappers. `FromJson` clones the caller's value, and the
converter retains its JSON payload and unknown fields without selecting a union
alternative. Composed unions retain the entire object, including union-branch
fields and additional intersection fields; the report explicitly records the
unvalidated intersection constraints. Named arrays of unions use a distinct
wrapper for each item. This
preserves structure and values, not original whitespace or property formatting.
The generated `schema-fidelity.json` records each exception and the actual
source/configuration hashes. Unknown IDs, duplicate entries, nonunion entries,
and this policy on the Fable target are rejected. Annotation-only aliases and
inline fields already represented as JSON values keep their existing paths.

Run the source-generation, compilation, and fake-HTTP regressions with:

```sh
python3 tests/response-contracts/run.py
```

The harness creates an isolated temporary output directory and runs both Debug
and Release clients. It makes no external HTTP requests. Whole-provider schema
generation and compilation are separate consumer acceptance gates; passing
these focused regressions does not claim that the full Cloudflare API is ready.

Request generation preserves structured JSON media types (`application/*+json`)
and uses their JSON codecs without rewriting the source content keys. Plain text
uses its declared charset, or UTF-8 when absent; binary and NDJSON request bodies
carry caller-supplied bytes. When an operation offers ordinary JSON alongside
other media, this client API selects the JSON representation. It does not expose
an overload for every alternative encoding. Unsupported request encodings fail
with an operation-specific diagnostic.

Composed JSON request records use the same inherited-field and required-field
collection as model declarations. An `allOf` reference with only annotations
retains its canonical named base instead of becoming an empty dictionary.
URL path values are escaped as values; form-urlencoded binary fields preserve
their submitted octets through percent encoding, without decoding them as UTF-8
or inventing base64. These are transport guarantees tested with an in-process
HTTP handler, not claims about a live provider accepting every source schema.

The explicit `x-hawaii-query-encoding: dotted-repeat` extension selects a
compatibility encoding for nested query objects and arrays. Repeated dotted
keys can lose array-element boundaries, and null has the same transport text as
an empty string. The consumer records the official SDK version and these limits;
this policy does not assert an injective OpenAPI encoding. Unknown or malformed
encoding extensions are errors.
