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

A binary array uses `MultipartFile list`. Bytes are sent directly, without JSON or base64 conversion. `FileName` supplies the `filename` parameter. `PartName = None` uses the schema property name; an explicit value overrides the part name independently of the filename. An override cannot claim another declared or dynamic field, including an omitted optional field. Repeated parts from one array retain the same owner and remain valid. Literal ASCII quotes and backslashes in names are quoted explicitly; non-ASCII names retain the framework's MIME convention, with collisions against MIME-looking literal names rejected.

The encoder honors each property's `encoding.contentType`. A single concrete file media type can supply the default. For multiple or wildcard choices, callers can select `ContentType`; `application/octet-stream` is the default only when the declared choices permit it. An incompatible selection fails before HTTP transmission.

The transport type is distinct from ordinary generated DTO aliases. Hawaii still maps a named `string`/`binary` schema component to a `string` alias, and its named array alias follows that DTO mapping. This change does not repair general binary DTO declaration mapping. Multipart parameters use the shared `MultipartFile` / `MultipartFile list` transport representation so file bytes and part metadata do not pass through those aliases.

Structured fields use JSON media types, including `application/json` by default. Primitive and structured arrays produce one part per item. Named metadata schema references keep their generated type. With explicit JSON encoding, that includes canonical union models and configured preserved-JSON wrappers. Inline object intersections and untyped inline objects use `JsonObject` (or `JsonNode` for unconstrained values) so added fields survive serialization. This is payload preservation, not field-level schema validation.

A multipart object with explicit string-valued `additionalProperties` uses a `MultipartTextField list`:

```fsharp
type MultipartTextField = {
    Name: string
    Value: string
    ContentType: string
}
```

Each field supplies its own name and concrete media type. The generator accepts the publisher's `encoding["*"].contentType` convention for these dynamic fields; without it the declared default is `text/plain`. The writer checks media choices against that declaration, rejects duplicate names, and sends the supplied string using the selected charset (UTF-8 by default). It does not infer base64 from prose, decode submitted base64 text, or add a filename. A binary-array additional property uses `MultipartFileField list`, whose entries pair a field `Name` with its `Files: MultipartFile list`. Fixed and dynamic fields can coexist. Required names constrained by `additionalProperties` become ordinary required parameters even when the source omits them from `properties`. Extra dynamic names may not collide with declared fields, and a dynamic file may not override its owning field name. Other dynamic value shapes, composed dynamic objects, or custom part headers currently produce diagnostics.

For example, [Workers direct uploads](https://developers.cloudflare.com/workers/static-assets/direct-upload/) require callers to supply base64 file contents, set the separate `base64` query parameter, and choose the media type that will later be served. These remain distinct caller choices. The generic text-field API preserves the chosen request representation; it does not replace it with a binary upload.

Construction failures dispose the incomplete multipart container and any part that could not be attached; the caller's existing request content remains unchanged.

For an optional body whose fields are all optional, omitting every field leaves the body absent. A required multipart body still carries a boundary when every optional field is omitted. Empty file arrays emit no file parts. Existing `byte[]` and `byte[] list` request helpers remain binary, without inventing filenames.

An `anyOf` containing exactly a plain string and a binary string uses the explicit `MultipartTextOrBinary.Text of string | Binary of byte[]` transport choice. Text defaults to `text/plain`; bytes default to `application/octet-stream`. Neither branch invents a filename. This represents the multipart wire choice, not a claim that the two alternatives are disjoint JSON schemas. Other alternative top-level or field schemas without an established JSON representation, incompatible intersection wire types, nested binary arrays, custom part headers and unsupported structured encodings fail explicitly. Enhanced composed multipart handling is currently for the F# target; the existing Fable simple-field path is retained.

The default repeated-name behavior and per-property content-type rules follow the [OpenAPI 3.0.3 multipart and Encoding Object sections](https://spec.openapis.org/oas/v3.0.3#special-considerations-for-multipart-content). Some APIs instead associate metadata with dynamic part names: the [Workers upload metadata documentation](https://developers.cloudflare.com/workers/configuration/multipart-upload-metadata/) defines `main_module` as a part name, and its [upload example](https://developers.cloudflare.com/workers/platform/infrastructure-as-code/) supplies that name and a filename explicitly. `PartName` makes this a caller choice without adding an API-specific generator rule.

Validation is in [the real HTTP regression](../tests/multipart-requests/README.md). Its fixture includes both a composed multipart schema and a JavaScript alternative; the multipart body is generated and transmitted without selecting the alternative.
