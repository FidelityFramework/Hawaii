module Hawaii.StatusRegression.Tests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Hawaii.StatusRegression
open Hawaii.StatusRegression.Types

type InspectRequest(inspect: HttpRequestMessage -> unit) =
    inherit HttpMessageHandler()
    override _.SendAsync(request: HttpRequestMessage, _: CancellationToken) =
        inspect request
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))

type Respond(status: int, body: byte[], mediaType: string) =
    inherit HttpMessageHandler()
    override _.SendAsync(request: HttpRequestMessage, _: CancellationToken) =
        if request.RequestUri.Host <> "test.invalid" then failwith "Unexpected request destination"
        let response = new HttpResponseMessage(enum<HttpStatusCode> status)
        response.Content <- new ByteArrayContent(body)
        response.Content.Headers.ContentType <- Headers.MediaTypeHeaderValue(mediaType)
        Task.FromResult response

let mutable passed = 0
let check label condition =
    if not condition then failwith label
    passed <- passed + 1

let withResponse status body mediaType action =
    use handler = new Respond(status, body, mediaType)
    use http = new HttpClient(handler, BaseAddress = Uri "https://test.invalid")
    action (StatusRegressionClient http)

let json status body action = withResponse status (Encoding.UTF8.GetBytes(body: string)) "application/json" action
let result (task: Task<'a>) = task.GetAwaiter().GetResult()

let decode<'a> text = Hawaii.StatusRegression.Http.Serializer.deserialize<'a> text
let encode value = Hawaii.StatusRegression.Http.Serializer.serialize value

[<EntryPoint>]
let main _ =
    json 200 "{\"errors\":[{\"code\":7,\"message\":\"detail\"}],\"message\":\"failure\",\"result\":{\"name\":\"typed\"}}" (fun client ->
        match result (client.Envelope()) with
        | Envelope.OK payload ->
            check "allOf response keeps inherited typed errors" (payload.errors.Length = 1 && payload.errors[0].code = 7)
            check "allOf response keeps its named result" (payload.result.name = "typed")
        )
    let missingRequiredString =
        try decode<FailureEnvelope> "{\"errors\":[]}" |> ignore; false
        with :? System.Text.Json.JsonException -> true
    check "required string fields cannot silently deserialize as null" missingRequiredString
    json 200 "{\"message\":\"ok\"}" (fun client ->
        match result (client.Dispatch()) with
        | Dispatch.OK payload -> check "200 JSON body" (payload.message = "ok")
        | _ -> failwith "200 dispatch")
    json 404 "{\"message\":\"missing\"}" (fun client ->
        match result (client.Dispatch()) with
        | Dispatch.NotFound payload -> check "exact code takes precedence over 4XX" (payload.message = "missing")
        | _ -> failwith "404 dispatch")
    for code in [ 400; 401; 499 ] do
        json code "\"client error\"" (fun client ->
            match result (client.Dispatch()) with
            | Dispatch.Status4XX(actual, payload) -> check "4XX keeps actual status and decoded body" (actual = code && payload = "client error")
            | _ -> failwith "4XX dispatch")
    for code in [ 500; 503; 599 ] do
        json code "\"server error\"" (fun client ->
            match result (client.Dispatch()) with
            | Dispatch.Status5XX(actual, payload) -> check "5XX keeps actual status and decoded body" (actual = code && payload = "server error")
            | _ -> failwith "5XX dispatch")
    for code in [ 201; 302 ] do
        json code "\"fallback\"" (fun client ->
            match result (client.Dispatch()) with
            | Dispatch.DefaultResponse(actual, payload) -> check "explicit default decodes its own body" (actual = code && payload = "fallback")
            | _ -> failwith "default dispatch")
    json 204 "" (fun client ->
        match result (client.Empty()) with
        | Empty.NoContent -> check "204 has no fabricated payload" true)
    for code in [ 200; 400; 503 ] do
        json code "\"not empty\"" (fun client ->
            let rejected =
                try result (client.Empty()) |> ignore; false
                with ex -> ex.Message.Contains(sprintf "Unexpected HTTP status %d" code)
            check "sole 204 response does not absorb unknown statuses" rejected)
    json 429 "\"limited\"" (fun client ->
        match result (client.Exact()) with
        | Exact.TooManyRequests body -> check "valid numeric status outside old whitelist" (body = "limited")
        | _ -> failwith "429 dispatch")
    json 433 "\"custom\"" (fun client ->
        match result (client.Exact()) with
        | Exact.Status433 body -> check "non-enum valid HTTP status" (body = "custom")
        | _ -> failwith "433 dispatch")
    json 503 "\"unknown\"" (fun client ->
        let rejected =
            try result (client.Exact()) |> ignore; false
            with ex -> ex.Message.Contains "Unexpected HTTP status 503"
        check "last declared response is not an implicit default" rejected)
    let bytes = [| 0uy; 255uy; 128uy; 65uy |]
    withResponse 200 bytes "application/octet-stream" (fun client ->
        match result (client.Binary()) with
        | Binary.OK body -> check "binary success remains byte-exact" (body = bytes)
        | _ -> failwith "binary dispatch")
    json 502 "{\"message\":\"down\"}" (fun client ->
        match result (client.Binary()) with
        | Binary.DefaultResponse(actual, body) -> check "binary endpoint default decodes JSON error" (actual = 502 && body.message = "down")
        | _ -> failwith "binary default dispatch")
    json 200 "[{\"name\":\"root\",\"legacy\":\"kept\",\"children\":[{\"name\":\"child\"}]}]" (fun client ->
        match result (client.Tree()) with
        | GetTree.OK payload ->
            let tree: Tree = payload
            check "named recursive arrays retain one declaration identity"
                (tree.Head.children.Value.Head.name = "child" && tree.Head.legacy = Some "kept"))
    json 200 "{\"name\":\"root\",\"element\":{\"name\":\"child\",\"element\":{\"name\":\"leaf\"}}}" (fun client ->
        match result (client.Definition()) with
        | GetDefinition.OK payload ->
            let childName: string = payload.element.Value.name
            let leafName: string = payload.element.Value.element.Value.name
            check "recursive allOf keeps inherited required fields" (childName = "child" && leafName = "leaf"))
    json 200 "{\"left\":{\"hyphen\":\"left\"},\"right\":{\"underscore\":7},\"$metadata\":{\"value\":\"meta\"},\"content\":{\"application/json\":{\"*\":{\"name\":\"wire\"}}}}" (fun client ->
        match result (client.Names()) with
        | GetNames.OK payload ->
            check "schema-name normalization preserves distinct declarations and JSON wire keys"
                (payload.left.hyphen = "left" && payload.right.underscore = 7 && payload.``$metadata``.value = "meta"
                 && payload.content.``application/json``.Value.``*``.Value.name = Some "wire"))
    json 200 "[1,\"two\",{\"three\":3}]" (fun client ->
        match result (client.AnyItems()) with
        | GetAnyItems.OK payload ->
            let values: AnyItems = payload
            check "explicitly unconstrained array items retain JSON values"
                (values.Count = 3 && values.[1].GetValue<string>() = "two" && values.[2].["three"].GetValue<int>() = 3))
    json 200 "{\"left\":\"known\",\"unknown\":{\"number\":123456789012345678901234567890}}" (fun client ->
        match result (client.Preserved()) with
        | Preserved.OK payload ->
            let canonical: AmbiguousPayload = payload
            check "preserved JSON retains unknown fields and numeric tokens"
                (canonical.JsonValue.GetProperty("unknown").GetProperty("number").GetRawText() = "123456789012345678901234567890")
            let encoded = Hawaii.StatusRegression.Http.Serializer.serialize canonical
            use decoded = System.Text.Json.JsonDocument.Parse encoded
            check "preserved JSON round trip writes the raw object shape"
                (decoded.RootElement.GetProperty("left").GetString() = "known"
                 && decoded.RootElement.GetProperty("unknown").ValueKind = System.Text.Json.JsonValueKind.Object))
    let retained =
        use document = System.Text.Json.JsonDocument.Parse "{\"outside\":true}"
        AmbiguousPayload.FromJson document.RootElement
    check "preserved JSON factory clones caller-owned documents" (retained.JsonValue.GetProperty("outside").GetBoolean())
    json 200 "{\"payload\":{\"outside\":true}}" (fun client ->
        match result (client.PayloadBox()) with
        | GetPayloadBox.OK box ->
            let canonical: AmbiguousPayload = box.payload
            check "fields reuse the canonical preserved wrapper" (canonical.JsonValue.GetProperty("outside").GetBoolean()))
    json 200 "[\"text\",{\"unknown\":true}]" (fun client ->
        match result (client.Payloads()) with
        | GetPayloads.OK payload ->
            let canonical: Payloads = payload
            check "array item wrappers preserve heterogeneous JSON without selecting a branch"
                (canonical.[0].JsonValue.GetString() = "text" && canonical.[1].JsonValue.GetProperty("unknown").GetBoolean()))
    for text in [ "null"; "true"; "123456789012345678901234567890"; "\"scalar\"" ] do
        let value = Hawaii.StatusRegression.Http.Serializer.deserialize<AmbiguousPayload> text
        let encoded = Hawaii.StatusRegression.Http.Serializer.serialize value
        check "preservation converter retains scalar and null JSON without claiming schema membership" (encoded = text)
    json 200 "[{\"name\":\"root\"}]" (fun client ->
        match result (client.GetDefinitionAliases()) with
        | GetDefinitionAliases.OK payload ->
            let canonical: Definition list = payload
            check "annotation-only allOf array elements reuse their canonical type" (canonical.Head.name = "root"))
    let standalone: TagA = decode "{\"kind\":\"a\",\"value\":\"shared\"}"
    use standaloneJson = System.Text.Json.JsonDocument.Parse(encode standalone)
    check "canonical discriminator member retains its standalone wire field" (standaloneJson.RootElement.GetProperty("kind").GetString() = "a")
    let tagged = Tagged.Variant1 standalone
    use taggedJson = System.Text.Json.JsonDocument.Parse(encode tagged)
    check "tagged union serializes canonical member exactly once"
        (taggedJson.RootElement.EnumerateObject() |> Seq.filter (fun p -> p.Name = "kind") |> Seq.length = 1)
    match decode<Tagged> "{\"kind\":\"b\",\"count\":7}" with
    | Tagged.Variant2 payload -> check "required literal discriminator selects canonical member" (payload.count = 7)
    | _ -> failwith "wrong tagged union branch"
    for text in [ "{\"value\":\"missing tag\"}"; "{\"kind\":\"unknown\"}"; "null" ] do
        let rejected = try decode<Tagged> text |> ignore; false with :? System.Text.Json.JsonException -> true
        check "unmatched discriminators are diagnosed" rejected
    match decode<InlineTagged> "{\"kind\":\"a\",\"value\":\"inline\",\"extra\":3}" with
    | InlineTagged.Variant1 payload -> check "inline allOf alternatives retain inherited required fields" (payload.value = "inline" && payload.extra = Some 3)
    | _ -> failwith "wrong inline union branch"
    for text in [ "42"; "\"text\"" ] do
        let value = decode<ByKind> text
        check "disjoint JSON kinds preserve the wire representation" (encode value = text)
    let recursive: RecursiveValue = decode "{\"nested\":[null,true,\"text\",17]}"
    use recursiveJson = System.Text.Json.JsonDocument.Parse(encode recursive)
    check "recursive disjoint JSON unions retain canonical object and array types"
        (recursiveJson.RootElement.GetProperty("nested").GetArrayLength() = 4 && recursiveJson.RootElement.GetProperty("nested").[0].ValueKind = System.Text.Json.JsonValueKind.Null)
    let inlineArray = decode<InlineArray> "{\"values\":[17,\"text\"]}"
    use inlineArrayJson = System.Text.Json.JsonDocument.Parse(encode inlineArray)
    check "inline union array elements do not collapse to strings"
        (inlineArrayJson.RootElement.GetProperty("values").[0].GetInt32() = 17 && inlineArrayJson.RootElement.GetProperty("values").[1].GetString() = "text")
    for text in [ "\"summary\""; "\"tags\""; "\"a-b\""; "\"a_b\"" ] do
        check "reserved and colliding enum names retain distinct wire values" (encode (decode<ReservedEnums> text) = text)
    use parameterHandler = new InspectRequest(fun request ->
        check "scalar wrapper path parameters keep the unquoted wire value" (request.RequestUri.AbsolutePath = "/parameters/id-17")
        let query = Uri.UnescapeDataString request.RequestUri.Query
        check "deprecated enum-list query parameters remain callable" (query.Contains("values=one&values=two"))
        check "explicit JSON query content retains its object-array payload" (query.Contains("filter=[{\"key\":\"value\"}]"))
        check "dotted repeat is an explicit compatibility encoding"
            (query.Contains("dotted.key=model&dotted.value=a&dotted.value=b&dotted.key=success&dotted.value=true"))
        check "explode false keeps comma-separated array encoding" (query.Contains("comma=a,b")))
    use parameterHttp = new HttpClient(parameterHandler, BaseAddress = Uri "https://test.invalid")
    let parameterClient = new StatusRegressionClient(parameterHttp)
    let identifier = decode<AmbiguousPayload> "\"id-17\""
    let enums: EnumValues = decode "[\"one\",\"two\"]"
    let filters = [ System.Text.Json.Nodes.JsonNode.Parse("{\"key\":\"value\"}").AsObject() ]
    let dotted = [
        System.Text.Json.Nodes.JsonNode.Parse("{\"key\":\"model\",\"value\":[\"a\",\"b\"]}").AsObject()
        System.Text.Json.Nodes.JsonNode.Parse("{\"key\":\"success\",\"value\":[true]}").AsObject()
    ]
    result (parameterClient.Parameters(identifier, values = enums, filter = filters, dotted = dotted, comma = [ "a"; "b" ])) |> ignore
    let invalidParameter = try retained.Format() |> ignore; false with :? InvalidOperationException -> true
    check "complex JSON cannot silently become a scalar URL parameter" invalidParameter
    use untypedHandler = new InspectRequest(fun request ->
        check "unconstrained path schemas retain JSON identity with scalar transport" (request.RequestUri.AbsolutePath = "/unconstrained/source-id")
        let query = Uri.UnescapeDataString request.RequestUri.Query
        check "date-time array queries use repeated keys" (query.Contains("dates=2026-09-06T12:34:00.0000000+00:00&dates=2026-09-07T12:34:00.0000000+00:00"))
        check "form object queries use their wire property names" (query.Contains("start=2026-09-01") && query.Contains("end=2026-09-06") && not(query.Contains("end_="))))
    use untypedHttp = new HttpClient(untypedHandler, BaseAddress = Uri "https://test.invalid")
    let dateRange = decode<DateRange> "{\"start\":\"2026-09-01\",\"end\":\"2026-09-06\"}"
    let dates = [ DateTimeOffset.Parse "2026-09-06T12:34:00Z"; DateTimeOffset.Parse "2026-09-07T12:34:00Z" ]
    result ((StatusRegressionClient untypedHttp).UnconstrainedParameters(System.Text.Json.Nodes.JsonValue.Create("source-id"), dates = dates, range = dateRange)) |> ignore
    use annotatedHandler = new InspectRequest(fun request ->
        check "annotation-only allOf paths retain their scalar base" (request.RequestUri.AbsolutePath = "/annotated/identity")
        check "annotation-only allOf query types retain their date-time base" (Uri.UnescapeDataString(request.RequestUri.Query).Contains("since=2026-09-06T12:34:00.0000000+00:00")))
    use annotatedHttp = new HttpClient(annotatedHandler, BaseAddress = Uri "https://test.invalid")
    result ((StatusRegressionClient annotatedHttp).AnnotatedParameters("identity", since = dates.Head)) |> ignore
    let inspectBody media expected action =
        use handler = new InspectRequest(fun request ->
            check "declared request media type retained" (request.Content.Headers.ContentType.MediaType = media)
            check "request body encoding retained" (result (request.Content.ReadAsByteArrayAsync()) = expected))
        use http = new HttpClient(handler, BaseAddress = Uri "https://test.invalid")
        action (StatusRegressionClient http)
    inspectBody "application/vnd.example+json" (Encoding.UTF8.GetBytes(encode standalone)) (fun client -> result (client.Vendor(standalone)) |> ignore)
    inspectBody "application/merge-patch+json" (Encoding.UTF8.GetBytes(encode standalone)) (fun client -> result (client.Merge(standalone)) |> ignore)
    inspectBody "text/plain" (Encoding.UTF8.GetBytes "select 'café'\n") (fun client -> result (client.Sql("select 'café'\n")) |> ignore)
    let ndjson = Encoding.UTF8.GetBytes "{\"id\":1}\n{\"id\":2}\n"
    inspectBody "application/x-ndjson" ndjson (fun client -> result (client.Ndjson(ndjson)) |> ignore)
    withResponse 200 (Encoding.UTF8.GetBytes(encode standalone)) "application/scim+json" (fun client ->
        match result (client.Scim(standalone)) with
        | Scim.OK payload -> check "structured JSON response media retains canonical payload" (payload.value = standalone.value))
    inspectBody "application/x-www-form-urlencoded" (Encoding.ASCII.GetBytes "count=7&name=caf%C3%A9+%26+tea&image=%00%FF%80A") (fun client ->
        result (client.Form(name = "café & tea", count = 7, image = bytes)) |> ignore)
    use escapedHandler = new InspectRequest(fun request ->
        check "path values cannot inject segments or query syntax" (request.RequestUri.AbsolutePath = "/annotated/a%2Fb%3Fq%3Dx" && request.RequestUri.Query = ""))
    use escapedHttp = new HttpClient(escapedHandler, BaseAddress = Uri "https://test.invalid")
    result ((StatusRegressionClient escapedHttp).AnnotatedParameters("a/b?q=x")) |> ignore
    let composed: ComposedBodyPayload = decode "{\"kind\":\"a\",\"value\":\"shared\",\"extra\":7}"
    check "composed request bodies retain inherited and refined required fields" (composed.value = "shared" && composed.extra = 7)
    inspectBody "application/json" (Encoding.UTF8.GetBytes(encode composed)) (fun client -> result (client.ComposedBody(composed)) |> ignore)
    let composedUnion = decode<ComposedUnion> "{\"kind\":\"a\",\"value\":\"must survive\",\"position\":3}"
    let sameCanonicalWrapper: ComposedAlias = composedUnion
    check "composed schema references retain one wrapper identity" (obj.ReferenceEquals(sameCanonicalWrapper, composedUnion))
    use composedUnionJson = System.Text.Json.JsonDocument.Parse(encode composedUnion)
    check "composed union preserves base branch and added fields together"
        (composedUnionJson.RootElement.GetProperty("kind").GetString() = "a"
         && composedUnionJson.RootElement.GetProperty("value").GetString() = "must survive"
         && composedUnionJson.RootElement.GetProperty("position").GetInt32() = 3)
    printfn "Passed %d HTTP status/body regression checks." passed
    0
