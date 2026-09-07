module HttpLibrary

let content = """namespace {projectName}.Http

open System
open System.Net.Http
open System.Globalization
open System.Collections.Generic
open System.Text
open System.Threading

module Serializer =
    open System.Text.Json
    open System.Text.Json.Serialization
    let private fsharpOptions =
        JsonFSharpOptions.Default()
            .WithUnionUnwrapFieldlessTags()
            .WithSkippableOptionFields()
            .WithAllowOverride(true)
    let options =
        let o = JsonSerializerOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)
        // Options-level converters precede type attributes. Leave explicitly
        // attributed schema converters to System.Text.Json's normal resolution.
        let fallback = JsonFSharpConverter(fsharpOptions)
        o.Converters.Add({ new JsonConverterFactory() with
            override _.CanConvert(t) =
                not (t.IsDefined(typeof<JsonConverterAttribute>, false)) && fallback.CanConvert(t)
            override _.CreateConverter(t, options) = fallback.CreateConverter(t, options) })
        o
    let serialize<'t> (value: 't) = JsonSerializer.Serialize(value, options)
    let deserialize<'t> (content: string) = JsonSerializer.Deserialize<'t>(content, options)

    let scalarText (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.String -> value.GetString()
        | JsonValueKind.Number | JsonValueKind.True | JsonValueKind.False -> value.GetRawText()
        | JsonValueKind.Null -> ""
        | _ -> invalidArg "value" "This parameter encoding requires scalar values"

    let commaParameter value =
        use document = JsonDocument.Parse(serialize value)
        if document.RootElement.ValueKind = JsonValueKind.Array then
            document.RootElement.EnumerateArray() |> Seq.map scalarText |> String.concat ","
        else scalarText document.RootElement

    let scalarParameter value =
        use document = JsonDocument.Parse(serialize value)
        if document.RootElement.ValueKind = JsonValueKind.Null then invalidArg "value" "A non-null scalar URL parameter is required"
        scalarText document.RootElement

    let formObject value =
        use document = JsonDocument.Parse(serialize value)
        if document.RootElement.ValueKind <> JsonValueKind.Object then invalidArg "value" "An object query parameter is required"
        [ for field in document.RootElement.EnumerateObject() -> field.Name, scalarText field.Value ]

    // An explicit compatibility encoding, not a general OpenAPI array rule.
    // Repeated dotted keys do not retain array-element boundaries, and null
    // has the same transport text as an empty string.
    let dottedParameters key value =
        use document = JsonDocument.Parse(serialize value)
        let rec flatten key (value: JsonElement) = [
            match value.ValueKind with
            | JsonValueKind.Array ->
                for item in value.EnumerateArray() do yield! flatten key item
            | JsonValueKind.Object ->
                for field in value.EnumerateObject() do yield! flatten (key + "." + field.Name) field.Value
            | _ -> yield key, scalarText value
        ]
        flatten key document.RootElement

[<RequireQualifiedAccess>]
type OpenApiValue =
    | Int of int
    | Int64 of int64
    | String of string
    | Bool of bool
    | Double of double
    | Float of float32
    | List of OpenApiValue list

/// A binary multipart value with its transport identity. PartName overrides
/// the schema property name only when the caller explicitly supplies it.
type MultipartFile = {
    Bytes: byte[]
    FileName: string
    ContentType: string option
    PartName: string option
}

/// A multipart string/binary alternative, with branch-specific media defaults.
[<RequireQualifiedAccess>]
type MultipartTextOrBinary =
    | Text of string
    | Binary of byte[]

/// A dynamically named multipart string value. Value is transmitted as supplied;
/// a source contract requiring base64 text remains the caller's responsibility.
type MultipartTextField = {
    Name: string
    Value: string
    ContentType: string
}

/// One dynamically named array of binary parts.
type MultipartFileField = {
    Name: string
    Files: MultipartFile list
}

type MultiPartFormData =
    | Primitive of OpenApiValue
    | File of byte[]
    | Files of MultipartFile list
    | BinaryValues of byte[] list
    | Json of string list
    | TextFields of MultipartTextField list
    | FileFields of MultipartFileField list
    | Encoded of string * MultiPartFormData

type RequestPart =
    | Query of string * OpenApiValue
    | QueryPairs of (string * string) list
    | Path of string * OpenApiValue
    | Header of string * OpenApiValue
    | MultiPartFormData of string * MultiPartFormData
    | MultipartBody
    | MultipartDeclaredFields of string list
    | UrlEncodedFormData of string * OpenApiValue
    | UrlEncodedBinary of string * byte[]
    | JsonContent of string
    | BinaryContent of byte[]
    | RawContent of string * byte[]
    | Ignore

    static member queryJson<'t>(key: string, value: 't) = Query(key, OpenApiValue.String(Serializer.serialize value))
    static member queryComma<'t>(key: string, value: 't) = Query(key, OpenApiValue.String(Serializer.commaParameter value))
    static member queryDotted<'t>(key: string, value: 't) = QueryPairs(Serializer.dottedParameters key value)
    static member queryForm<'t>(key: string, value: 't) = QueryPairs(Serializer.formObject value)
    static member query(key: string, value: int) = Query(key, OpenApiValue.Int value)
    static member query(key: string, values: int list) = Query(key, OpenApiValue.List [ for value in values -> OpenApiValue.Int value ])
    static member query(key: string, value: int64) = Query(key, OpenApiValue.Int64 value)
    static member query(key: string, value: string) = Query(key, OpenApiValue.String value)
    static member query(key: string, value: string option) = 
        match value with 
        | Some text -> Query(key, OpenApiValue.String text)
        | None -> Ignore
    static member query(key: string, value: int option) = 
        match value with 
        | Some number -> Query(key, OpenApiValue.Int number)
        | None -> Ignore
    static member query(key: string, value: bool option) = 
        match value with 
        | Some flag -> Query(key, OpenApiValue.Bool flag)
        | None -> Ignore
    static member query(key: string, value: double option) =
        match value with
        | Some number -> Query(key, OpenApiValue.Double number)
        | None -> Ignore
    static member inline query< ^a when ^a : (member Format: unit -> string)>(key: string, value: ^a) : RequestPart =
        let format = (^a: (member Format: unit -> string) (value))
        Query(key, OpenApiValue.String format)
    static member inline query< ^a when ^a : (member Format: unit -> string)>(key: string, values: ^a list) : RequestPart =
        Query(key, OpenApiValue.List [ for value in values -> OpenApiValue.String ((^a: (member Format: unit -> string) (value))) ])
    static member inline query< ^a when ^a : (member Format: unit -> string)>(key: string, value: ^a option) : RequestPart =
        match value with
        | None -> Ignore
        | Some instance ->
            let format = (^a: (member Format: unit -> string) (instance))
            Query(key, OpenApiValue.String format)
    static member query(key: string, value: int64 option) = 
        match value with 
        | Some number -> Query(key, OpenApiValue.Int64 number)
        | None -> Ignore

    static member query(key: string, values: string list) = Query(key, OpenApiValue.List [ for value in values -> OpenApiValue.String value ])
    static member query(key: string, values: Guid list) = Query(key, OpenApiValue.List [ for value in values -> OpenApiValue.String (value.ToString()) ])
    static member query(key: string, values: int64 list) = Query(key, OpenApiValue.List [ for value in values -> OpenApiValue.Int64 value ])
    static member query(key: string, values: double list) = Query(key, OpenApiValue.List [ for value in values -> OpenApiValue.Double value ])
    static member query(key: string, values: float32 list) = Query(key, OpenApiValue.List [ for value in values -> OpenApiValue.Float value ])
    static member query(key: string, values: bool list) = Query(key, OpenApiValue.List [ for value in values -> OpenApiValue.Bool value ])
    static member query(key: string, values: DateTimeOffset list) = Query(key, OpenApiValue.List [ for value in values -> OpenApiValue.String (value.ToString("O")) ])
    static member query(key: string, value: System.Text.Json.Nodes.JsonNode) = Query(key, OpenApiValue.String(Serializer.scalarParameter value))
    static member query(key: string, value: bool) = Query(key, OpenApiValue.Bool value)
    static member query(key: string, value: double) = Query(key, OpenApiValue.Double value)
    static member query(key: string, value: float32) = Query(key, OpenApiValue.Float value)
    static member query(key: string, value: Guid) = Query(key, OpenApiValue.String (value.ToString()))
    static member query(key: string, value: DateTimeOffset) = Query(key, OpenApiValue.String (value.ToString("O")))
    static member path(key: string, value: int) = Path(key, OpenApiValue.Int value)
    static member path(key: string, value: System.Text.Json.Nodes.JsonNode) = Path(key, OpenApiValue.String(Serializer.scalarParameter value))
    static member path(key: string, value: int64) = Path(key, OpenApiValue.Int64 value)
    static member path(key: string, value: string) = Path(key, OpenApiValue.String value)
    static member path(key: string, value: bool) = Path(key, OpenApiValue.Bool value)
    static member path(key: string, value: double) = Path(key, OpenApiValue.Double value)
    static member path(key: string, value: float32) = Path(key, OpenApiValue.Float value)
    static member path(key: string, value: Guid) = Path(key, OpenApiValue.String (value.ToString()))
    static member path(key: string, value: DateTimeOffset) = Path(key, OpenApiValue.String (value.ToString("O")))
    static member path(key: string, values: string list) = Path(key, OpenApiValue.List [ for value in values -> OpenApiValue.String value ])
    static member path(key: string, values: Guid list) = Path(key, OpenApiValue.List [ for value in values -> OpenApiValue.String (value.ToString()) ])
    static member path(key: string, values: int list) = Path(key, OpenApiValue.List [ for value in values -> OpenApiValue.Int value ])
    static member path(key: string, values: int64 list) = Path(key, OpenApiValue.List [ for value in values -> OpenApiValue.Int64 value ])
    static member inline path< ^a when ^a : (member Format: unit -> string)>(key: string, value: ^a) : RequestPart =
        let format = (^a: (member Format: unit -> string) (value))
        Path(key, OpenApiValue.String format)
    static member inline path< ^a when ^a : (member Format: unit -> string)>(key: string, value: ^a option) : RequestPart =
        match value with
        | None -> Ignore
        | Some instance ->
            let format = (^a: (member Format: unit -> string) (instance))
            Path(key, OpenApiValue.String format)
    static member multipartTextOrBinary(key: string, value: MultipartTextOrBinary) =
        match value with
        | MultipartTextOrBinary.Text text -> MultiPartFormData(key, Primitive(OpenApiValue.String text))
        | MultipartTextOrBinary.Binary bytes -> MultiPartFormData(key, File bytes)
    static member multipartTextOrBinary(key: string, contentType: string, value: MultipartTextOrBinary) =
        match value with
        | MultipartTextOrBinary.Text text -> MultiPartFormData(key, Encoded(contentType, Primitive(OpenApiValue.String text)))
        | MultipartTextOrBinary.Binary bytes -> MultiPartFormData(key, Encoded(contentType, File bytes))
    static member multipartFileFields(key: string, contentType: string, values: MultipartFileField list) =
        MultiPartFormData(key, Encoded(contentType, FileFields values))
    static member multipartFields(key: string, contentType: string, values: MultipartTextField list) =
        MultiPartFormData(key, Encoded(contentType, TextFields values))
    static member multipartBinary(key: string, contentType: string, value: MultipartFile) =
        MultiPartFormData(key, Encoded(contentType, Files [value]))
    static member multipartBinary(key: string, contentType: string, values: MultipartFile list) =
        MultiPartFormData(key, Encoded(contentType, Files values))
    static member multipartJson<'t>(key: string, contentType: string, value: 't) =
        MultiPartFormData(key, Encoded(contentType, Json [Serializer.serialize value]))
    static member multipartJsonMany<'t>(key: string, contentType: string, values: 't list) =
        MultiPartFormData(key, Encoded(contentType, Json (List.map Serializer.serialize values)))
    static member multipartScalar<'t>(key: string, contentType: string, value: 't) =
        MultiPartFormData(key, Encoded(contentType, Primitive(OpenApiValue.String(Serializer.scalarParameter value))))
    static member multipartScalarMany<'t>(key: string, contentType: string, values: 't list) =
        MultiPartFormData(key, Encoded(contentType, Primitive(OpenApiValue.List [ for value in values -> OpenApiValue.String(Serializer.scalarParameter value) ])))
    static member multipartFormData(key: string, value: int) =
        MultiPartFormData(key, Primitive(OpenApiValue.Int value))
    static member multipartFormData(key: string, value: int64) =
        MultiPartFormData(key, Primitive(OpenApiValue.Int64 value))
    static member multipartFormData(key: string, value: string) =
        MultiPartFormData(key, Primitive(OpenApiValue.String value))
    static member multipartFormData(key: string, value: bool) =
        MultiPartFormData(key, Primitive(OpenApiValue.Bool value))
    static member multipartFormData(key: string, value: double) =
        MultiPartFormData(key, Primitive(OpenApiValue.Double value))
    static member multipartFormData(key: string, value: float32) =
        MultiPartFormData(key, Primitive(OpenApiValue.Float value))
    static member multipartFormData(key: string, value: Guid) =
        MultiPartFormData(key, Primitive(OpenApiValue.String (value.ToString())))
    static member multipartFormData(key: string, value: DateTimeOffset) =
        MultiPartFormData(key, Primitive(OpenApiValue.String (value.ToString("O"))))
    static member multipartFormData(key: string, value: byte[]) =
        MultiPartFormData(key, File value)
    static member multipartFormData(key: string, values: byte[] list) =
        MultiPartFormData(key, BinaryValues values)
    // Structured fields retain JSON encoding; binary lists have an exact overload.
    static member multipartFormData<'t>(key: string, value: 't) =
        MultiPartFormData(key, Json [Serializer.serialize value])
    static member multipartFormData(key: string, values: string list) = MultiPartFormData(key, Primitive(OpenApiValue.List [ for value in values -> OpenApiValue.String value ]))
    static member multipartFormData(key: string, values: Guid list) = MultiPartFormData(key, Primitive(OpenApiValue.List [ for value in values -> OpenApiValue.String (value.ToString()) ]))
    static member multipartFormData(key: string, values: int list) = MultiPartFormData(key, Primitive(OpenApiValue.List [ for value in values -> OpenApiValue.Int value ]))
    static member multipartFormData(key: string, values: int64 list) = MultiPartFormData(key, Primitive(OpenApiValue.List [ for value in values -> OpenApiValue.Int64 value ]))
    static member urlEncodedFormData(key: string, value: byte[]) = UrlEncodedBinary(key, value)
    static member urlEncodedFormData(key: string, value: int) = UrlEncodedFormData(key, OpenApiValue.Int value)
    static member urlEncodedFormData(key: string, value: int64) = UrlEncodedFormData(key, OpenApiValue.Int64 value)
    static member urlEncodedFormData(key: string, value: string) = UrlEncodedFormData(key, OpenApiValue.String value)
    static member urlEncodedFormData(key: string, value: bool) = UrlEncodedFormData(key, OpenApiValue.Bool value)
    static member urlEncodedFormData(key: string, value: double) = UrlEncodedFormData(key, OpenApiValue.Double value)
    static member urlEncodedFormData(key: string, value: float32) = UrlEncodedFormData(key, OpenApiValue.Float value)
    static member urlEncodedFormData(key: string, value: Guid) = UrlEncodedFormData(key, OpenApiValue.String (value.ToString()))
    static member urlEncodedFormData(key: string, value: DateTimeOffset) = UrlEncodedFormData(key, OpenApiValue.String (value.ToString("O")))
    static member header(key: string, value: int) = Header(key, OpenApiValue.Int value)
    static member header(key: string, value: int64) = Header(key, OpenApiValue.Int64 value)
    static member header(key: string, value: string) = Header(key, OpenApiValue.String value)
    static member header(key: string, value: bool) = Header(key, OpenApiValue.Bool value)
    static member header(key: string, value: double) = Header(key, OpenApiValue.Double value)
    static member header(key: string, value: float32) = Header(key, OpenApiValue.Float value)
    static member header(key: string, value: Guid) = Header(key, OpenApiValue.String (value.ToString()))
    static member header(key: string, value: DateTimeOffset) = Header(key, OpenApiValue.String (value.ToString("O")))
    static member jsonContent<'t>(content: 't) = JsonContent(Serializer.serialize content)
    static member rawContent(mediaType: string, content: byte[]) = RawContent(mediaType, content)
    static member textContent(mediaType: string, content: string) =
        let header = Headers.MediaTypeHeaderValue.Parse mediaType
        let encoding = if String.IsNullOrEmpty header.CharSet then Encoding.UTF8 else Encoding.GetEncoding(header.CharSet.Trim('"'))
        RawContent(mediaType, encoding.GetBytes(content))
    static member jsonMediaContent<'t>(mediaType: string, content: 't) = RequestPart.textContent(mediaType, Serializer.serialize content)

    static member binaryContent(content: byte[]) = BinaryContent(content)

module OpenApiHttp =
    let rec serializeValue = function
        | OpenApiValue.String value -> value
        | OpenApiValue.Int value -> value.ToString(CultureInfo.InvariantCulture)
        | OpenApiValue.Int64 value -> value.ToString(CultureInfo.InvariantCulture)
        | OpenApiValue.Double value -> value.ToString(CultureInfo.InvariantCulture)
        | OpenApiValue.Float value -> value.ToString(CultureInfo.InvariantCulture)
        | OpenApiValue.Bool value -> value.ToString().ToLower()
        | OpenApiValue.List values ->
            values
            |> List.map serializeValue
            |> String.concat ","

    let applyPathParts (path: string) (parts: RequestPart list) =
        let applyPart (currentPath: string) (part: RequestPart) : string =
            match part with
            | Path(key, value) ->
                let encoded =
                    match value with
                    | OpenApiValue.List values -> values |> List.map (serializeValue >> Uri.EscapeDataString) |> String.concat ","
                    | scalar -> Uri.EscapeDataString(serializeValue scalar)
                currentPath.Replace("{" + key + "}", encoded)
            | _ -> currentPath

        parts |> List.fold applyPart path

    let applyQueryStringParameters (currentPath: string) (parts: RequestPart list) =
        let cleanedPath = currentPath.TrimEnd '/'
        let queryParams =
            parts
            |> List.collect (function
                | Query(key, OpenApiValue.List values) -> [ for value in values -> key, serializeValue value ]
                | Query(key, value) -> [key, serializeValue value]
                | QueryPairs pairs -> pairs
                | _ -> [])

        if List.isEmpty queryParams then
            cleanedPath
        else
            let combinedParamters =
                queryParams
                |> List.map (fun (key, value) -> $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}")
                |> String.concat "&"

            cleanedPath + "?" + combinedParamters

    let applyJsonContent (parts: RequestPart list) (httpRequest: HttpRequestMessage) =
        for part in parts do
            match part with
            | JsonContent content ->
                httpRequest.Content <- new StringContent(content, Encoding.UTF8, "application/json")
            | _ -> ()

        httpRequest

    let applyBinaryContent (parts: RequestPart list) (httpRequest: HttpRequestMessage) =
        for part in parts do
            match part with
            | BinaryContent content ->
                httpRequest.Content <- new ByteArrayContent(content)
                httpRequest.Content.Headers.ContentType <- Headers.MediaTypeHeaderValue("application/octet-stream")
            | RawContent(mediaType, content) ->
                httpRequest.Content <- new ByteArrayContent(content)
                httpRequest.Content.Headers.ContentType <- Headers.MediaTypeHeaderValue.Parse mediaType
            | _ -> ()

        httpRequest

    let applyHeaders (parts: RequestPart list) (httpRequest: HttpRequestMessage) =
        for part in parts do
            match part with
            | Header(key, value) ->
                httpRequest.Headers.Add(key, serializeValue value)
            | _ ->
                ()

        httpRequest

    let applyAcceptHeader (httpRequest: HttpRequestMessage) =
        httpRequest.Headers.Accept.ParseAdd "application/json"
        httpRequest

    /// Flattens the response headers (and the content headers) of an HTTP
    /// response into a simple list of key/value string pairs.
    let extractResponseHeaders (response: HttpResponseMessage) : (string * string) list =
        [
            for header in response.Headers do
                for value in header.Value do
                    header.Key, value
            if not (isNull response.Content) then
                for header in response.Content.Headers do
                    for value in header.Value do
                        header.Key, value
        ]

    let applyMultiPartFormData (parts: RequestPart list) (httpRequest: HttpRequestMessage) =
        let formParts =
            parts
            |> List.choose (function
                | MultiPartFormData (key, value) -> Some(key, value)
                | _ -> None
            )

        let requiredBody = parts |> List.exists (function MultipartBody -> true | _ -> false)
        if formParts.IsEmpty && not requiredBody then
            httpRequest
        else
            let boundary = Guid.NewGuid().ToString("N")
            let multipartFormData = new MultipartFormDataContent(boundary)
            try
                let concreteMedia (allowed: string) (requested: string option) =
                    let choices = allowed.Split(',') |> Array.map (fun value -> value.Trim())
                    let matches (actual: string) (candidate: string) =
                        let actual = actual.Split(';').[0].Trim()
                        let candidate = candidate.Split(';').[0].Trim()
                        candidate = "*/*" || actual.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                        (candidate.EndsWith("/*", StringComparison.Ordinal) && actual.StartsWith(candidate.Substring(0, candidate.Length - 1), StringComparison.OrdinalIgnoreCase))
                    let selected =
                        match requested with
                        | Some media -> media
                        | None when choices.Length = 1 && not (choices.[0].Contains "*") -> choices.[0]
                        | None when choices |> Array.exists (matches "application/octet-stream") -> "application/octet-stream"
                        | None -> invalidArg "ContentType" "A multipart file must select one of the declared content types"
                    if selected.Contains "," || selected.Contains "*" || not (choices |> Array.exists (matches selected)) then
                        invalidArg "ContentType" "The multipart file content type is not permitted by its encoding"
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse selected |> ignore
                    selected
                let disposition (name: string) (fileName: string option) =
                    let header = System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
                    if name |> Seq.exists (fun character -> int character > 127) then
                        // Retain the framework's existing MIME convention for non-ASCII names.
                        header.Name <- name
                    else
                        // Name's setter strips bounding quotes. Supply a validated quoted
                        // parameter directly so literal quotes/backslashes retain identity.
                        let quoted = "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
                        header.Parameters.Add(System.Net.Http.Headers.NameValueHeaderValue("name", quoted))
                    match fileName with
                    | Some filename -> header.FileName <- filename; header.FileNameStar <- filename
                    | None -> ()
                    header
                let wireOwnerName (name: string) =
                    // This also detects a literal MIME-looking name colliding with a
                    // framework-encoded non-ASCII name.
                    if name.StartsWith("=?", StringComparison.Ordinal) then (disposition name None).Name
                    else name
                let addText (key: string) (media: string) (text: string) =
                    let header = System.Net.Http.Headers.MediaTypeHeaderValue.Parse media
                    let encoding =
                        if String.IsNullOrWhiteSpace header.CharSet then Encoding.UTF8
                        else Encoding.GetEncoding(header.CharSet.Trim('"'), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                    let content = new StringContent(text, encoding)
                    content.Headers.ContentType <- header
                    try
                        content.Headers.ContentDisposition <- disposition key None
                        multipartFormData.Add(content)
                    with _ -> content.Dispose(); reraise()
                let rec isDynamic = function
                    | Encoded(_, value) -> isDynamic value
                    | TextFields _ | FileFields _ -> true
                    | _ -> false
                let owners = System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
                let claimName owner name =
                    if String.IsNullOrWhiteSpace name then invalidArg "Name" "A multipart field name is required"
                    let wireName = wireOwnerName name
                    match owners.TryGetValue wireName with
                    | true, existing when existing <> owner -> invalidArg "Name" "Multipart part name belongs to another field"
                    | true, _ -> () // Repeated parts of one array keep their owner.
                    | false, _ -> owners.Add(wireName, owner)
                for part in parts do
                    match part with
                    | MultipartDeclaredFields declared -> for name in declared do claimName name name
                    | _ -> ()
                for key, value in formParts do
                    if not (isDynamic value) then claimName key key
                let reserveDynamicName name =
                    if String.IsNullOrWhiteSpace name then invalidArg "Name" "A multipart field name is required"
                    let wireName = wireOwnerName name
                    if owners.ContainsKey wireName then invalidArg "Name" "Dynamic multipart object fields must have unique names distinct from declared fields"
                    owners.Add(wireName, name)
                let rec reserveDynamic = function
                    | Encoded(_, value) -> reserveDynamic value
                    | TextFields fields -> for field in fields do reserveDynamicName field.Name
                    | FileFields fields -> for field in fields do reserveDynamicName field.Name
                    | _ -> ()
                for _, value in formParts do reserveDynamic value
                let addContent (content: HttpContent) add =
                    try add ()
                    with _ -> content.Dispose(); reraise()
                let rec add key media part =
                    match part with
                    | Encoded(contentType, value) -> add key (Some contentType) value
                    | Primitive(OpenApiValue.List values) ->
                        for value in values do add key media (Primitive value)
                    | Primitive value -> addText key (defaultArg media "text/plain") (serializeValue value)
                    | Json values ->
                        for value in values do addText key (defaultArg media "application/json") value
                    | File bytes ->
                        let header = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(defaultArg media "application/octet-stream")
                        let content = new ByteArrayContent(bytes)
                        content.Headers.ContentType <- header
                        addContent content (fun () ->
                            content.Headers.ContentDisposition <- disposition key None
                            multipartFormData.Add(content))
                    | BinaryValues values ->
                        for bytes in values do add key media (File bytes)
                    | TextFields fields ->
                        for field in fields do
                            if isNull field.Value then nullArg "Value"
                            if String.IsNullOrWhiteSpace field.ContentType then invalidArg "ContentType" "A concrete multipart field content type is required"
                            let contentType = concreteMedia (defaultArg media "text/plain") (Some field.ContentType)
                            addText field.Name contentType field.Value
                    | FileFields fields ->
                        for field in fields do
                            if field.Files |> List.exists (fun file -> file.PartName |> Option.exists ((<>) field.Name)) then
                                invalidArg "PartName" "A dynamic file must retain its owning field name"
                            add field.Name media (Files field.Files)
                    | Files values ->
                        for file in values do
                            if isNull file.Bytes then nullArg "Bytes"
                            if String.IsNullOrWhiteSpace file.FileName then invalidArg "FileName" "A multipart filename is required"
                            let name = defaultArg file.PartName key
                            claimName key name
                            let contentType = concreteMedia (defaultArg media "application/octet-stream") file.ContentType
                            let content = new ByteArrayContent(file.Bytes)
                            content.Headers.ContentType <- System.Net.Http.Headers.MediaTypeHeaderValue.Parse contentType
                            addContent content (fun () ->
                                content.Headers.ContentDisposition <- disposition name (Some file.FileName)
                                multipartFormData.Add(content))
                for (key, part) in formParts do add key None part

                if multipartFormData |> Seq.isEmpty then
                    // The framework's empty container writes a nameless empty part.
                    // An empty form contains only its closing delimiter.
                    let content = new ByteArrayContent(Encoding.ASCII.GetBytes("--" + boundary + "--\r\n"))
                    content.Headers.ContentType <- multipartFormData.Headers.ContentType
                    multipartFormData.Dispose()
                    httpRequest.Content <- content
                else
                    httpRequest.Content <- multipartFormData
                httpRequest
            with _ ->
                multipartFormData.Dispose()
                reraise()

    // Percent-encode the submitted octets directly. Binary form fields must
    // not pass through UTF-8 decoding or acquire an invented base64 encoding.
    let encodeFormOctets (bytes: byte[]) =
        let result = StringBuilder()
        for value in bytes do
            if (value >= 65uy && value <= 90uy) || (value >= 97uy && value <= 122uy)
               || (value >= 48uy && value <= 57uy) || value = 45uy || value = 46uy || value = 95uy || value = 126uy then
                result.Append(char value) |> ignore
            elif value = 32uy then result.Append('+') |> ignore
            else result.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture)) |> ignore
        result.ToString()

    let applyUrlEncodedFormData (parts: RequestPart list) (httpRequest: HttpRequestMessage) =
        let formParts =
            parts
            |> List.choose (function
                | UrlEncodedFormData(key, value) -> Some(key, Encoding.UTF8.GetBytes(serializeValue value))
                | UrlEncodedBinary(key, value) -> Some(key, value)
                | _ -> None)
        if formParts.IsEmpty then httpRequest
        else
            let contents =
                [ for key, value in formParts -> encodeFormOctets(Encoding.UTF8.GetBytes key) + "=" + encodeFormOctets value ]
                |> String.concat "&"
            httpRequest.Content <- new ByteArrayContent(Encoding.ASCII.GetBytes contents)
            httpRequest.Content.Headers.ContentType <- Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded")
            httpRequest

    let sendAsync (httpClient: HttpClient) (method: HttpMethod) (path: string) (parts: RequestPart list) {cancellationArgument} =
        let cancellationToken = Option.defaultValue CancellationToken.None cancellationToken
        let modifiedPath = applyPathParts path parts
        let modifiedQueryParams = applyQueryStringParameters modifiedPath parts
        let requestUri = Uri(httpClient.BaseAddress.OriginalString.TrimEnd '/' + modifiedQueryParams)
        let request = new HttpRequestMessage(RequestUri=requestUri, Method=method)
        let populatedRequest =
            request
            |> applyJsonContent parts
            |> applyBinaryContent parts
            |> applyUrlEncodedFormData parts
            |> applyMultiPartFormData parts
            |> applyHeaders parts

        {asyncBuilder} {
            let! response = {getResponse}
            let! content = {getContent}
            return (response.StatusCode, extractResponseHeaders response, content)
        }

    let sendBinaryAsync (httpClient: HttpClient) (method: HttpMethod) (path: string) (parts: RequestPart list) {cancellationArgument} =
        let cancellationToken = Option.defaultValue CancellationToken.None cancellationToken
        let modifiedPath = applyPathParts path parts
        let modifiedQueryParams = applyQueryStringParameters modifiedPath parts
        let requestUri = Uri(httpClient.BaseAddress.OriginalString.TrimEnd '/' + modifiedQueryParams)
        let request = new HttpRequestMessage(RequestUri=requestUri, Method=method)
        let populatedRequest =
            request
            |> applyJsonContent parts
            |> applyBinaryContent parts
            |> applyUrlEncodedFormData parts
            |> applyMultiPartFormData parts
            |> applyHeaders parts

        {asyncBuilder} {
            let! response = {getResponse}
            let! content = {getBinaryContent}
            return (response.StatusCode, extractResponseHeaders response, content)
        }

    let sendStreamAsync (httpClient: HttpClient) (method: HttpMethod) (path: string) (parts: RequestPart list) {cancellationArgument} =
        let cancellationToken = Option.defaultValue CancellationToken.None cancellationToken
        let modifiedPath = applyPathParts path parts
        let modifiedQueryParams = applyQueryStringParameters modifiedPath parts
        let requestUri = Uri(httpClient.BaseAddress.OriginalString.TrimEnd '/' + modifiedQueryParams)
        let request = new HttpRequestMessage(RequestUri=requestUri, Method=method)
        let populatedRequest =
            request
            |> applyJsonContent parts
            |> applyBinaryContent parts
            |> applyUrlEncodedFormData parts
            |> applyMultiPartFormData parts
            |> applyHeaders parts

        {asyncBuilder} {
            let! response = {getResponse}
            let! content = {getStreamContent}
            return (response.StatusCode, extractResponseHeaders response, content)
        }

    let getAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendAsync httpClient HttpMethod.Get path parts {cancellationParameter}

    let get (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        getAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let getBinaryAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendBinaryAsync httpClient HttpMethod.Get path parts {cancellationParameter}

    let getBinary (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        getBinaryAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let getStreamAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendStreamAsync httpClient HttpMethod.Get path parts {cancellationParameter}

    let getStream (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        getStreamAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let postAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendAsync httpClient HttpMethod.Post path parts {cancellationParameter}

    let post (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        postAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let postBinaryAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendBinaryAsync httpClient HttpMethod.Post path parts {cancellationParameter}

    let postBinary (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        postBinaryAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let postStreamAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendStreamAsync httpClient HttpMethod.Post path parts {cancellationParameter}

    let postStream (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        postStreamAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let deleteAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendAsync httpClient HttpMethod.Delete path parts {cancellationParameter}

    let delete (httpClient: HttpClient) (path: string) (parts: RequestPart list)  {cancellationArgument} =
        deleteAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let deleteBinaryAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendBinaryAsync httpClient HttpMethod.Delete path parts {cancellationParameter}

    let deleteBinary (httpClient: HttpClient) (path: string) (parts: RequestPart list)  {cancellationArgument} =
        deleteBinaryAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let deleteStreamAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendStreamAsync httpClient HttpMethod.Delete path parts {cancellationParameter}

    let deleteStream (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        deleteStreamAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let putAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendAsync httpClient HttpMethod.Put path parts {cancellationParameter}

    let put (httpClient: HttpClient) (path: string) (parts: RequestPart list)  {cancellationArgument} =
        putAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let putBinaryAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendBinaryAsync httpClient HttpMethod.Put path parts {cancellationParameter}

    let putBinary (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        putBinaryAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let putStreamAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendStreamAsync httpClient HttpMethod.Put path parts {cancellationParameter}

    let putStream (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        putStreamAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let patchAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendAsync httpClient (HttpMethod "PATCH") path parts {cancellationParameter}

    let patch (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        patchAsync httpClient path parts {cancellationParameter} 
        {convertSync}

    let patchBinaryAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendBinaryAsync httpClient (HttpMethod "PATCH") path parts {cancellationParameter}

    let patchBinary (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        patchBinaryAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let patchStreamAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendStreamAsync httpClient (HttpMethod "PATCH") path parts {cancellationParameter}

    let patchStream (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        patchStreamAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let headAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendAsync httpClient (HttpMethod "HEAD") path parts {cancellationParameter}
         
    let head (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        headAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let headBinaryAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendBinaryAsync httpClient (HttpMethod "HEAD") path parts {cancellationParameter}

    let headBinary (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        headBinaryAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let headStreamAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendStreamAsync httpClient (HttpMethod "HEAD") path parts {cancellationParameter}

    let headStream (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        headStreamAsync httpClient path parts {cancellationParameter}
        {convertSync}

    // OpenAPI 3.2 QUERY method - a safe, idempotent request that carries a body
    let queryAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendAsync httpClient (HttpMethod "QUERY") path parts {cancellationParameter}

    let query (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        queryAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let queryBinaryAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendBinaryAsync httpClient (HttpMethod "QUERY") path parts {cancellationParameter}

    let queryBinary (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        queryBinaryAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let queryStreamAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendStreamAsync httpClient (HttpMethod "QUERY") path parts {cancellationParameter}

    let queryStream (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        queryStreamAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let optionsAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendAsync httpClient (HttpMethod "OPTIONS") path parts {cancellationParameter}

    let options (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        optionsAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let optionsBinaryAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendBinaryAsync httpClient (HttpMethod "OPTIONS") path parts {cancellationParameter}

    let optionsBinary (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        optionsBinaryAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let optionsStreamAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendStreamAsync httpClient (HttpMethod "OPTIONS") path parts {cancellationParameter}

    let optionsStream (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        optionsStreamAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let traceAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendAsync httpClient (HttpMethod "TRACE") path parts {cancellationParameter}

    let trace (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        traceAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let traceBinaryAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendBinaryAsync httpClient (HttpMethod "TRACE") path parts {cancellationParameter}

    let traceBinary (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        traceBinaryAsync httpClient path parts {cancellationParameter}
        {convertSync}

    let traceStreamAsync (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        sendStreamAsync httpClient (HttpMethod "TRACE") path parts {cancellationParameter}

    let traceStream (httpClient: HttpClient) (path: string) (parts: RequestPart list) {cancellationArgument} =
        traceStreamAsync httpClient path parts {cancellationParameter}
        {convertSync}
"""

let library isTask projectName =
    let convertSync =
        if isTask
        then "|> Async.AwaitTask |> Async.RunSynchronously"
        else "|> Async.RunSynchronously"

    let getResponse =
        if isTask
        then "httpClient.SendAsync(populatedRequest, cancellationToken)"
        else "Async.AwaitTask(httpClient.SendAsync(populatedRequest, cancellationToken))"

    let getContent =
        if isTask
        then "response.Content.ReadAsStringAsync()"
        else "Async.AwaitTask(response.Content.ReadAsStringAsync())"

    let getBinaryContent =
        if isTask
        then "response.Content.ReadAsByteArrayAsync()"
        else "Async.AwaitTask(response.Content.ReadAsByteArrayAsync())"

    let getStreamContent =
        if isTask
        then "response.Content.ReadAsStreamAsync()"
        else "Async.AwaitTask(response.Content.ReadAsStreamAsync())"

    content
        .Replace("{projectName}", projectName)
        .Replace("{asyncBuilder}", if isTask then "task" else "async")
        .Replace("{cancellationArgument}", "(cancellationToken: CancellationToken option)")
        .Replace("{cancellationParameter}", "cancellationToken")
        .Replace("{convertSync}", convertSync)
        .Replace("{getResponse}", getResponse)
        .Replace("{getContent}", getContent)
        .Replace("{getBinaryContent}", getBinaryContent)
        .Replace("{getStreamContent}", getStreamContent)

let fableContent = """namespace {projectName}.Http

open System
open Thoth.Json
open Fable.SimpleHttp
open Fable.Core
open Fable.Core.JsInterop
open Browser.Types
open System.Runtime.CompilerServices

/// Utilities for working with binary data types in the browser
module Utilities =
    [<Emit("new FileReader()")>]
    /// Creates a new instance of a FileReader
    let createFileReader() : FileReader = jsNative
    [<Emit("new Uint8Array($0)")>]
    let createUInt8Array(x: 'a) : byte[]  = jsNative
    /// Creates a Blob from the given input string
    [<Emit("new Blob([$0.buffer], { type: $1 })")>]
    let createBlobFromBytesAndMimeType (value: byte[]) (mimeType: string) : Blob = jsNative
    [<Emit("window.URL.createObjectURL($0)")>]
    /// Creates an object URL (also known as data url) from a Blob
    let createObjectUrl (blob: Blob) : string = jsNative
    [<Emit "URL.revokeObjectURL($0)">]
    /// Releases an existing object URL which was previously created by calling createObjectURL(). Call this method when you've finished using an object URL to let the browser know not to keep the reference to the file any longer.
    let revokeObjectUrl (dataUrl: string) : unit = jsNative
    [<Emit "$0 instanceof Uint8Array">]
    /// Returns whether the input byte array is a typed array of type Uint8Array
    let isUInt8Array (data: byte[]) : bool = jsNative
    /// Creates a typed byte array of binary data if it not already typed
    let toUInt8Array(data: byte[]) : byte[] =
        if isUInt8Array data
        then data
        else createUInt8Array data

    /// Creates a Blob from the given input string
    [<Emit("new Blob([$0.buffer], { type: 'text/plain' })")>]
    let fromBinaryEncodedText (value: byte[]) : Blob = jsNative

    /// Asynchronously reads the blob data content as string
    let readBytesAsText (bytes: byte[]) : Async<string> =
        Async.FromContinuations <| fun (resolve, _, _) ->
            let reader = createFileReader()
            reader.onload <- fun _ ->
                if reader.readyState = FileReaderState.DONE
                then resolve (unbox reader.result)

            reader.readAsText(fromBinaryEncodedText bytes)



[<AutoOpenAttribute>]
module BrowserFileExtensions =

    type File with

        /// Asynchronously reads the File content as byte[]
        member instance.ReadAsByteArray() =
            Async.FromContinuations <| fun (resolve, _, _) ->
                let reader = Utilities.createFileReader()
                reader.onload <- fun _ ->
                    if reader.readyState = FileReaderState.DONE
                    then resolve(Utilities.createUInt8Array(reader.result))

                reader.readAsArrayBuffer(instance)

        /// Asynchronously reads the File content as a data url string
        member instance.ReadAsDataUrl() =
            Async.FromContinuations <| fun (resolve, _, _) ->
                let reader = Utilities.createFileReader()
                reader.onload <- fun _ ->
                    if reader.readyState = FileReaderState.DONE
                    then resolve(unbox<string> reader.result)

                reader.readAsDataURL(instance)

        /// <summary>Asynchronously reads the File contents as text</summary>
        member instance.ReadAsText() =
            Async.FromContinuations <| fun (resolve, _, _) ->
                let reader = Utilities.createFileReader()
                reader.onload <- fun _ ->
                    if reader.readyState = FileReaderState.DONE
                    then resolve(unbox<string> reader.result)

                reader.readAsText(instance)

[<Extension>]
type ByteArrayExtensions =
    [<Extension>]
    /// <summary>Saves the binary content as a file using the provided file name.</summary>
    static member SaveFileAs(content: byte[], fileName: string) =

        if String.IsNullOrWhiteSpace(fileName) then
            ()
        else
        let mimeType = "application/octet-stream"
        let binaryData = Utilities.toUInt8Array content
        let blob = Utilities.createBlobFromBytesAndMimeType binaryData mimeType
        let dataUrl = Utilities.createObjectUrl blob
        let anchor =  (Browser.Dom.document.createElement "a")
        anchor?style <- "display: none"
        anchor?href <- dataUrl
        anchor?download <- fileName
        anchor?rel <- "noopener"
        anchor.click()
        // clean up
        anchor.remove()
        // clean up the created object url because it is being kept in memory
        Browser.Dom.window.setTimeout(unbox(fun () -> Utilities.revokeObjectUrl(dataUrl)), 40 * 1000)
        |> ignore

    [<Extension>]
    /// Saves the binary content as a file using the provided file name.
    static member SaveFileAs(content: byte[], fileName: string, mimeType: string) =

        if String.IsNullOrWhiteSpace(fileName) then
            ()
        else
        let binaryData = Utilities.toUInt8Array content
        let blob = Utilities.createBlobFromBytesAndMimeType binaryData mimeType
        let dataUrl = Utilities.createObjectUrl blob
        let anchor =  Browser.Dom.document.createElement "a"
        anchor?style <- "display: none"
        anchor?href <- dataUrl
        anchor?download <- fileName
        anchor?rel <- "noopener"
        anchor.click()
        // clean up element
        anchor.remove()
        // clean up the created object url because it is being kept in memory
        Browser.Dom.window.setTimeout(unbox(fun () -> Utilities.revokeObjectUrl(dataUrl)), 40 * 1000)
        |> ignore

    [<Extension>]
    /// Converts the binary content into a data url by first converting it to a Blob of type "application/octet-stream" and reading it as a data url.
    static member AsDataUrl(content: byte[]) : string =
        let binaryData = Utilities.toUInt8Array content
        let blob = Utilities.createBlobFromBytesAndMimeType binaryData "application/octet-stream"
        let dataUrl = Utilities.createObjectUrl blob
        dataUrl

    [<Extension>]
    /// Converts the binary content into a data url by first converting it to a Blob of the provided mime-type and reading it as a data url.
    static member AsDataUrl(content: byte[], mimeType:string) : string =
        let binaryData = Utilities.toUInt8Array content
        let blob = Utilities.createBlobFromBytesAndMimeType binaryData mimeType
        let dataUrl = Utilities.createObjectUrl blob
        dataUrl

module Serializer =
    /// Thoth.Json `extra` coders, generated alongside the schema types. Holds a
    /// custom encoder/decoder for every `oneOf` + `discriminator` union so that
    /// those unions round-trip as the flat OpenAPI discriminator JSON shape.
    /// When the schema declares no discriminator unions this is `Extra.empty`.
    let extraCoders = {projectName}.Types.ThothCoders.extraCoders
    let inline serialize<'t> (value: 't) : string =
        Encode.Auto.toString<'t>(value, extra = extraCoders)
    let inline deserialize<'t> (content: string) : 't =
        match Decode.Auto.fromString<'t>(content, extra = extraCoders) with
        | Ok value -> value
        | Error error -> failwithf "Error while deserializing JSON content: %s" error

[<RequireQualifiedAccess>]
type OpenApiValue =
    | Int of int
    | Int64 of int64
    | String of string
    | Bool of bool
    | Double of double
    | Float of float32
    | List of OpenApiValue list

type MultiPartFormData =
    | Primitive of OpenApiValue
    | File of File

type RequestPart =
    | Query of string * OpenApiValue
    | Path of string * OpenApiValue
    | Header of string * OpenApiValue
    | MultiPartFormData of string * MultiPartFormData
    | UrlEncodedFormData of string * OpenApiValue
    | JsonContent of string
    | BinaryContent of byte[]
    | Ignore

    static member query(key: string, value: int) = Query(key, OpenApiValue.Int value)
    static member query(key: string, values: int list) = Query(key, OpenApiValue.List [ for value in values -> OpenApiValue.Int value ])
    static member query(key: string, value: int64) = Query(key, OpenApiValue.Int64 value)
    static member query(key: string, value: string) = Query(key, OpenApiValue.String value)
    static member query(key: string, value: string option) =
        match value with
        | Some text -> Query(key, OpenApiValue.String text)
        | None -> Ignore
    static member query(key: string, value: int option) =
        match value with
        | Some number -> Query(key, OpenApiValue.Int number)
        | None -> Ignore
    static member query(key: string, value: bool option) =
        match value with
        | Some flag -> Query(key, OpenApiValue.Bool flag)
        | None -> Ignore
    static member query(key: string, value: double option) =
        match value with
        | Some number -> Query(key, OpenApiValue.Double number)
        | None -> Ignore
    static member inline query< ^a when ^a : (member Format: unit -> string)>(key: string, value: ^a) : RequestPart =
        let format = (^a: (member Format: unit -> string) (value))
        Query(key, OpenApiValue.String format)
    static member inline query< ^a when ^a : (member Format: unit -> string)>(key: string, value: ^a option) : RequestPart =
        match value with
        | None -> Ignore
        | Some instance ->
            let format = (^a: (member Format: unit -> string) (instance))
            Query(key, OpenApiValue.String format)
    static member query(key: string, value: int64 option) =
        match value with
        | Some number -> Query(key, OpenApiValue.Int64 number)
        | None -> Ignore

    static member query(key: string, values: string list) = Query(key, OpenApiValue.List [ for value in values -> OpenApiValue.String value ])
    static member query(key: string, values: Guid list) = Query(key, OpenApiValue.List [ for value in values -> OpenApiValue.String (value.ToString()) ])
    static member query(key: string, value: bool) = Query(key, OpenApiValue.Bool value)
    static member query(key: string, value: double) = Query(key, OpenApiValue.Double value)
    static member query(key: string, value: float32) = Query(key, OpenApiValue.Float value)
    static member query(key: string, value: Guid) = Query(key, OpenApiValue.String (value.ToString()))
    static member query(key: string, value: DateTimeOffset) = Query(key, OpenApiValue.String (value.ToString("O")))
    static member path(key: string, value: int) = Path(key, OpenApiValue.Int value)
    static member path(key: string, value: int64) = Path(key, OpenApiValue.Int64 value)
    static member path(key: string, value: string) = Path(key, OpenApiValue.String value)
    static member path(key: string, value: bool) = Path(key, OpenApiValue.Bool value)
    static member path(key: string, value: double) = Path(key, OpenApiValue.Double value)
    static member path(key: string, value: float32) = Path(key, OpenApiValue.Float value)
    static member path(key: string, value: Guid) = Path(key, OpenApiValue.String (value.ToString()))
    static member path(key: string, value: DateTimeOffset) = Path(key, OpenApiValue.String (value.ToString("O")))
    static member path(key: string, values: string list) = Path(key, OpenApiValue.List [ for value in values -> OpenApiValue.String value ])
    static member path(key: string, values: Guid list) = Path(key, OpenApiValue.List [ for value in values -> OpenApiValue.String (value.ToString()) ])
    static member path(key: string, values: int list) = Path(key, OpenApiValue.List [ for value in values -> OpenApiValue.Int value ])
    static member path(key: string, values: int64 list) = Path(key, OpenApiValue.List [ for value in values -> OpenApiValue.Int64 value ])
    static member inline path< ^a when ^a : (member Format: unit -> string)>(key: string, value: ^a) : RequestPart =
        let format = (^a: (member Format: unit -> string) (value))
        Path(key, OpenApiValue.String format)
    static member inline path< ^a when ^a : (member Format: unit -> string)>(key: string, value: ^a option) : RequestPart =
        match value with
        | None -> Ignore
        | Some instance ->
            let format = (^a: (member Format: unit -> string) (instance))
            Path(key, OpenApiValue.String format)
    static member multipartFormData(key: string, value: int) =
        MultiPartFormData(key, Primitive(OpenApiValue.Int value))
    static member multipartFormData(key: string, value: int64) =
        MultiPartFormData(key, Primitive(OpenApiValue.Int64 value))
    static member multipartFormData(key: string, value: string) =
        MultiPartFormData(key, Primitive(OpenApiValue.String value))
    static member multipartFormData(key: string, value: bool) =
        MultiPartFormData(key, Primitive(OpenApiValue.Bool value))
    static member multipartFormData(key: string, value: double) =
        MultiPartFormData(key, Primitive(OpenApiValue.Double value))
    static member multipartFormData(key: string, value: float32) =
        MultiPartFormData(key, Primitive(OpenApiValue.Float value))
    static member multipartFormData(key: string, value: Guid) =
        MultiPartFormData(key, Primitive(OpenApiValue.String (value.ToString())))
    static member multipartFormData(key: string, value: DateTimeOffset) =
        MultiPartFormData(key, Primitive(OpenApiValue.String (value.ToString("O"))))
    static member multipartFormData(key: string, value: File) =
        MultiPartFormData(key, File value)
    // a structured (object/array/record) form-data field is sent as its JSON text;
    // the concrete overloads above win for scalars, this catches everything else
    static member inline multipartFormData<'t>(key: string, value: 't) =
        MultiPartFormData(key, Primitive(OpenApiValue.String(Serializer.serialize value)))
    static member multipartFormData(key: string, values: string list) = MultiPartFormData(key, Primitive(OpenApiValue.List [ for value in values -> OpenApiValue.String value ]))
    static member multipartFormData(key: string, values: Guid list) = MultiPartFormData(key, Primitive(OpenApiValue.List [ for value in values -> OpenApiValue.String (value.ToString()) ]))
    static member multipartFormData(key: string, values: int list) = MultiPartFormData(key, Primitive(OpenApiValue.List [ for value in values -> OpenApiValue.Int value ]))
    static member multipartFormData(key: string, values: int64 list) = MultiPartFormData(key, Primitive(OpenApiValue.List [ for value in values -> OpenApiValue.Int64 value ]))
    static member urlEncodedFormData(key: string, value: string) = UrlEncodedFormData(key, OpenApiValue.String value)
    static member urlEncodedFormData(key: string, value: bool) = UrlEncodedFormData(key, OpenApiValue.Bool value)
    static member urlEncodedFormData(key: string, value: double) = UrlEncodedFormData(key, OpenApiValue.Double value)
    static member urlEncodedFormData(key: string, value: float32) = UrlEncodedFormData(key, OpenApiValue.Float value)
    static member urlEncodedFormData(key: string, value: Guid) = UrlEncodedFormData(key, OpenApiValue.String (value.ToString()))
    static member urlEncodedFormData(key: string, value: DateTimeOffset) = UrlEncodedFormData(key, OpenApiValue.String (value.ToString("O")))
    static member header(key: string, value: int) = Header(key, OpenApiValue.Int value)
    static member header(key: string, value: int64) = Header(key, OpenApiValue.Int64 value)
    static member header(key: string, value: string) = Header(key, OpenApiValue.String value)
    static member header(key: string, value: bool) = Header(key, OpenApiValue.Bool value)
    static member header(key: string, value: double) = Header(key, OpenApiValue.Double value)
    static member header(key: string, value: float32) = Header(key, OpenApiValue.Float value)
    static member header(key: string, value: Guid) = Header(key, OpenApiValue.String (value.ToString()))
    static member header(key: string, value: DateTimeOffset) = Header(key, OpenApiValue.String (value.ToString("O")))
    static member inline jsonContent<'t>(content: 't) = JsonContent(Serializer.serialize content)
    static member binaryContent(content: byte[]) = BinaryContent(content)

module OpenApiHttp =
    let rec serializeValue = function
        | OpenApiValue.String value -> value
        | OpenApiValue.Int value -> string value
        | OpenApiValue.Int64 value -> string value
        | OpenApiValue.Double value -> string value
        | OpenApiValue.Float value -> string value
        | OpenApiValue.Bool value -> string value
        | OpenApiValue.List values ->
            values
            |> List.map serializeValue
            |> String.concat ","

    let applyPathParts (path: string) (parts: RequestPart list) =
        let applyPart (currentPath: string) (part: RequestPart) : string =
            match part with
            | Path(key, value) -> currentPath.Replace("{" + key + "}", serializeValue value)
            | _ -> currentPath

        parts |> List.fold applyPart path

    [<Emit "encodeURIComponent($0)">]
    let encodeURIComponent(queryValue: string) = jsNative

    let applyQueryStringParameters (currentPath: string) (parts: RequestPart list) =
        let cleanedPath = currentPath.TrimEnd '/'
        let queryParams =
            parts
            |> List.choose (function
                | Query(key, value) -> Some(key, value)
                | _ -> None)

        if List.isEmpty queryParams then
            cleanedPath
        else
            let combinedParamters =
                queryParams
                |> List.map (fun (key, value) -> $"{key}={encodeURIComponent(serializeValue value)}")
                |> String.concat "&"

            cleanedPath + "?" + combinedParamters

    let combineBasePath (basePath: string) (path: string) : string =
        basePath.TrimEnd '/' + "/" + path.TrimStart '/'

    let applyJsonRequestBody (parts: RequestPart list) (httpRequest: HttpRequest) =
        parts
        |> Seq.choose (function
            | JsonContent json ->
                httpRequest
                |> Http.header (Headers.contentType "application/json")
                |> Http.content (BodyContent.Text json)
                |> Some
            | _ -> None)
        |> Seq.tryHead
        |> Option.defaultValue httpRequest

    let applyMultipartFormData (parts: RequestPart list) (httpRequest: HttpRequest) =
        let formParts =
            parts
            |> List.choose(function
                | MultiPartFormData(key, value) -> Some(key, value)
                | _ -> None
            )

        if formParts |> List.isEmpty then
            httpRequest
        else
            let formData = FormData.create()
            for (key, value) in formParts do
                match value with
                | Primitive primitive -> formData.append(key, serializeValue primitive)
                | File file -> formData.append(key, file)

            httpRequest
            |> Http.content (BodyContent.Form formData)

    let applyUrlEncodedFormData (parts: RequestPart list) (httpRequest: HttpRequest) =
        parts
        |> List.choose(function
            | UrlEncodedFormData(key, value) ->
                Some $"{key}={encodeURIComponent(serializeValue value)}"
            | _ -> None)
        |> function
            | [] -> httpRequest
            | data ->
                httpRequest
                |> Http.header (Headers.contentType "application/x-www-form-urlencoded")
                |> Http.content (BodyContent.Text (data |> String.concat "&"))

    let applyHeaders (parts: RequestPart list) (httpRequest: HttpRequest) =
        parts
        |> List.choose (function 
            | RequestPart.Header (s, v) ->
                Fable.SimpleHttp.Header (s, serializeValue v)
                |> Some
            | _ -> None)
        |> function
            | [] -> httpRequest
            | headers ->
                httpRequest
                |> Http.headers headers

    /// Flattens the response headers exposed by Fable.SimpleHttp into a simple
    /// list of key/value string pairs.
    let extractResponseHeaders (response: HttpResponse) : (string * string) list =
        response.responseHeaders
        |> Map.toList

    let sendAsync (method: HttpMethod) (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) : Async<int * (string * string) list * string> =
        async {
            let requestPath = applyPathParts path parts
            let requestPathWithQuery = applyQueryStringParameters requestPath parts
            let fullPath = combineBasePath basePath requestPathWithQuery
            let! response =
                Http.request fullPath
                |> Http.method method
                |> applyJsonRequestBody parts
                |> applyMultipartFormData parts
                |> applyUrlEncodedFormData parts
                |> applyHeaders parts
                |> Http.headers extraHeaders
                |> Http.withCredentials true
                |> Http.send

            let status = response.statusCode
            let content = response.responseText
            return status, extractResponseHeaders response, content
        }

    let sendBinaryAsync (method: HttpMethod) (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) : Async<int * (string * string) list * byte[]> =
        async {
            let requestPath = applyPathParts path parts
            let requestPathWithQuery = applyQueryStringParameters requestPath parts
            let fullPath = combineBasePath basePath requestPathWithQuery
            let! response =
                Http.request fullPath
                |> Http.method method
                |> applyJsonRequestBody parts
                |> applyMultipartFormData parts
                |> applyUrlEncodedFormData parts
                |> Http.headers extraHeaders
                |> Http.overrideResponseType ResponseTypes.ArrayBuffer
                |> Http.withCredentials true
                |> Http.send

            match response.content with
            | ResponseContent.ArrayBuffer arrayBuffer ->
                let status = response.statusCode
                let content = Utilities.createUInt8Array arrayBuffer
                return status, extractResponseHeaders response, content
            | _ ->
                let status = response.statusCode
                return status, extractResponseHeaders response, [||]
        }

    let getAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendAsync GET basePath path extraHeaders parts

    let getBinaryAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendBinaryAsync GET basePath path extraHeaders parts

    let postAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendAsync POST basePath path extraHeaders parts

    let postBinaryAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendBinaryAsync POST basePath path extraHeaders parts

    let deleteAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendAsync DELETE basePath path extraHeaders parts

    let deleteBinaryAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendBinaryAsync DELETE basePath path extraHeaders parts

    let putAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendAsync PUT basePath path extraHeaders parts

    let putBinaryAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendBinaryAsync PUT basePath path extraHeaders parts

    let patchAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendAsync PATCH basePath path extraHeaders parts

    let patchBinaryAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendBinaryAsync PATCH basePath path extraHeaders parts

    let headAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendAsync HEAD basePath path extraHeaders parts

    let headBinaryAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendBinaryAsync HEAD basePath path extraHeaders parts

    // OpenAPI 3.2 QUERY method. Fable.SimpleHttp's HttpMethod has no QUERY case,
    // so the request falls back to POST - the closest method that carries a body.
    let queryAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendAsync POST basePath path extraHeaders parts

    let queryBinaryAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendBinaryAsync POST basePath path extraHeaders parts

    let optionsAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendAsync OPTIONS basePath path extraHeaders parts

    let optionsBinaryAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendBinaryAsync OPTIONS basePath path extraHeaders parts

    // The browser fetch/XHR APIs forbid the TRACE method, and Fable.SimpleHttp's
    // HttpMethod has no TRACE case, so this falls back to GET.
    let traceAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendAsync GET basePath path extraHeaders parts

    let traceBinaryAsync (basePath: string) (path: string) (extraHeaders: Header list) (parts: RequestPart list) =
        sendBinaryAsync GET basePath path extraHeaders parts
"""

let fableLibrary (projectName: string) = fableContent.Replace("{projectName}", projectName)
