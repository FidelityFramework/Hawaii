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
    let rejected =
        try
            result (client.Upload(metadata = value, files = [file binary "invalid.bin" (Some "image/jpeg") None])) |> ignore
            false
        with :? ArgumentException -> true
    if not rejected then failwith "A file media type outside its declared choices was accepted"
    printfn "Multipart generated client sent eight verified requests and rejected an undeclared file media type."
    0
