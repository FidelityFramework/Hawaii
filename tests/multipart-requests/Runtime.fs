module Hawaii.MultipartRegression.Tests

open System
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes
open Hawaii.MultipartRegression
open Hawaii.MultipartRegression.Http
open Hawaii.MultipartRegression.Types

let result (task: System.Threading.Tasks.Task<'a>) = task.GetAwaiter().GetResult()
let metadata (text: string) = JsonNode.Parse(text).AsObject()
let file bytes filename media partName : MultipartFile =
    { Bytes = bytes; FileName = filename; ContentType = media; PartName = partName }

[<EntryPoint>]
let main args =
    use http = new HttpClient(BaseAddress = Uri args.[0])
    let client = MultipartRegressionClient http
    let binary = [| 0uy; 255uy; 128uy; 65uy; 13uy; 10uy |]
    let wasm = [| 0uy; 97uy; 115uy; 109uy; 255uy |]
    let value = metadata """{"entry":"first.bin","annotations":{"label":"kept"},"future":{"enabled":true}}"""
    result (client.Upload(metadata = value,
                          files = [file binary "first.bin" None None; file wasm "second.wasm" (Some "application/wasm") None],
                          single = file [|137uy;80uy;78uy;71uy;0uy;255uy|] "image.png" None None,
                          labels = ["one"; "two"],
                          metadataItems = [{ label = "first" }; { label = "second" }],
                          untyped = metadata """{"message":"inline","extra":[1,2]}""")) |> ignore
    result (client.Upload(metadata = metadata """{"entry":"main.wasm","annotations":{"label":"dynamic"}}""",
                          files = [file wasm "module.wasm" (Some "application/wasm") (Some "main.wasm")])) |> ignore
    result (client.Upload(metadata = metadata """{"entry":"empty","annotations":{}}""", files = [])) |> ignore
    let named: NamedMetadata = { label = "canonical" }
    result (client.Named(metadata = named)) |> ignore
    result (OpenApiHttp.sendAsync http HttpMethod.Post "/legacy" [RequestPart.multipartFormData("files", [binary; wasm])] None) |> ignore
    result (client.Empty()) |> ignore
    use json = JsonDocument.Parse("""{"future":[true,12,{"nested":"retained"}],"unknown":null}""")
    let canonical = ArbitraryMetadata.FromJson(json.RootElement)
    let alias: MetadataAlias = canonical
    if not (obj.ReferenceEquals(canonical, alias)) then failwith "Metadata alias lost canonical identity"
    result (client.Union(value = MultipartTextOrBinary.Text "Hello λ", metadata = alias)) |> ignore
    result (client.Union(value = MultipartTextOrBinary.Binary binary, metadata = alias)) |> ignore
    let encoded = Convert.ToBase64String binary
    let fields: MultipartTextField list = [
        { Name = "hash_a"; Value = encoded; ContentType = "application/wasm" }
        { Name = "hash_b"; Value = "SGVsbG8g4rK7"; ContentType = "text/html" }
        { Name = "hash_c"; Value = ""; ContentType = "application/null" }
        { Name = "unicode"; Value = "λ"; ContentType = "text/plain; charset=utf-16" }
        { Name = "same"; Value = "unquoted"; ContentType = "text/plain" }
        { Name = "\"same\""; Value = "quoted"; ContentType = "text/plain" }
        { Name = "a\\b"; Value = "backslash"; ContentType = "text/plain" }
    ]
    result (client.Dynamic(body = fields)) |> ignore
    result (client.Dynamic(body = [])) |> ignore
    for invalid in [ [fields.Head; fields.Head]; [{fields.Head with ContentType = "*/*"}]; [{fields.Head with Name = ""}]; [{fields.Head with Name = "λ"}; {fields.Head with Name = "=?utf-8?B?zrs=?="}] ] do
        let rejected =
            try result (client.Dynamic(body = invalid)) |> ignore; false
            with :? ArgumentException -> true
        if not rejected then failwith "Invalid dynamic multipart fields were sent"
    let dynamicFiles: MultipartFileField list = [{Name = "extra"; Files = [file wasm "extra.wasm" None None]}]
    result (client.Mixed(metadata = named, files = [file binary "main.js" None None], body = dynamicFiles)) |> ignore
    for reserved in ["metadata"; "optional"] do
        let duplicate = [{dynamicFiles.Head with Name = reserved}]
        let rejectedDuplicate =
            try result (client.Mixed(metadata = named, files = [], body = duplicate)) |> ignore; false
            with :? ArgumentException -> true
        if not rejectedDuplicate then failwith "Dynamic files replaced a declared field"
    for reserved, extras in ["optional", []; "extra", dynamicFiles] do
        let rejectedOverride =
            try
                result (client.Mixed(metadata = named, files = [file binary "main.js" None (Some reserved)], body = extras)) |> ignore
                false
            with :? ArgumentException -> true
        if not rejectedOverride then failwith "PartName overrode another field owner"
    let rejectedOmitted =
        try result (client.Upload(metadata = value, files = [file binary "a.bin" None (Some "single")])) |> ignore; false
        with :? ArgumentException -> true
    if not rejectedOmitted then failwith "PartName overrode an omitted ordinary field"
    result (client.Upload(metadata = value,
                          files = [file binary "first.bin" None (Some "same"); file wasm "second.wasm" (Some "application/wasm") (Some "same")])) |> ignore
    // A failed construction must leave the caller's existing content owned by it.
    // The writer separately disposes its incomplete container and unattached part.
    use existingContent = new StringContent("untouched")
    use existingRequest = new HttpRequestMessage()
    existingRequest.Content <- existingContent
    for badField in [{fields.Head with Name = "bad-media"; ContentType = "*/*"}; {fields.Head with Name = "bad\r\nname"}] do
        let rejectedConstruction =
            try
                OpenApiHttp.applyMultiPartFormData [RequestPart.multipartFields("body", "*/*", [fields.Head; badField])] existingRequest |> ignore
                false
            with
            | :? ArgumentException
            | :? FormatException -> true
        if not rejectedConstruction then failwith "Invalid multipart construction was accepted"
        if not (obj.ReferenceEquals(existingContent, existingRequest.Content)) || existingContent.ReadAsStringAsync().Result <> "untouched" then
            failwith "Failed multipart construction changed caller-owned content"
    let rejected =
        try
            result (client.Upload(metadata = value, files = [file binary "invalid.bin" (Some "image/jpeg") None])) |> ignore
            false
        with :? ArgumentException -> true
    if not rejected then failwith "A file media type outside its declared choices was accepted"
    printfn "Multipart generated client sent twelve verified requests and rejected an undeclared file media type."
    0
