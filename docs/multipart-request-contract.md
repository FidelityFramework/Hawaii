# Multipart request contract

For the F# target, multipart request objects can compose their declared properties and required fields through `allOf`. Binary fields use one shared transport type in the generated HTTP namespace:

```fsharp
type MultipartFile = {
    Bytes: byte[]
    FileName: string
    ContentType: string option
    PartName: string option
}
```

A binary array uses `MultipartFile list`. Bytes are sent directly, without JSON or base64 conversion. `FileName` supplies the `filename` parameter. `PartName = None` uses the schema property name; an explicit value overrides the part name independently of the filename.

The encoder honors each property's `encoding.contentType`. A single concrete file media type can supply the default. For multiple or wildcard choices, callers can select `ContentType`; `application/octet-stream` is the default only when the declared choices permit it. An incompatible selection fails before HTTP transmission.

The transport type is distinct from ordinary generated DTO aliases. Hawaii still maps a named `string`/`binary` schema component to a `string` alias, and its named array alias follows that DTO mapping. This change does not repair general binary DTO declaration mapping. Multipart parameters use the shared `MultipartFile` / `MultipartFile list` transport representation so file bytes and part metadata do not pass through those aliases.

Structured fields use JSON media types, including `application/json` by default. Primitive and structured arrays produce one part per item. Named metadata schema references keep their generated type. With explicit JSON encoding, that includes canonical union models and configured preserved-JSON wrappers. Inline object intersections and untyped inline objects use `JsonObject` (or `JsonNode` for unconstrained values) so added fields survive serialization. This is payload preservation, not field-level schema validation.

For an optional body whose fields are all optional, omitting every field leaves the body absent. A required multipart body still carries a boundary when every optional field is omitted. Empty file arrays emit no file parts. Existing `byte[]` and `byte[] list` request helpers remain binary, without inventing filenames.

An `anyOf` containing exactly a plain string and a binary string uses the explicit `MultipartTextOrBinary.Text of string | Binary of byte[]` transport choice. Text defaults to `text/plain`; bytes default to `application/octet-stream`. Neither branch invents a filename. This represents the multipart wire choice, not a claim that the two alternatives are disjoint JSON schemas. Other alternative top-level or field schemas without an established JSON representation, incompatible intersection wire types, nested binary arrays, custom part headers and unsupported structured encodings fail explicitly. Enhanced composed multipart handling is currently for the F# target; the existing Fable simple-field path is retained.

The default repeated-name behavior and per-property content-type rules follow the [OpenAPI 3.0.3 multipart and Encoding Object sections](https://spec.openapis.org/oas/v3.0.3#special-considerations-for-multipart-content). Some APIs instead associate metadata with dynamic part names: the [Workers upload metadata documentation](https://developers.cloudflare.com/workers/configuration/multipart-upload-metadata/) defines `main_module` as a part name, and its [upload example](https://developers.cloudflare.com/workers/platform/infrastructure-as-code/) supplies that name and a filename explicitly. `PartName` makes this a caller choice without adding an API-specific generator rule.

Validation is in [the real HTTP regression](../tests/multipart-requests/README.md). Its fixture includes both a composed multipart schema and a JavaScript alternative; the multipart body is generated and transmitted without selecting the alternative.
