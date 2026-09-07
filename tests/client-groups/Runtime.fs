module GroupFixture.Tests

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open GroupFixture.Core.Types
open GroupFixture.First
open GroupFixture.Second

type Respond() =
    inherit HttpMessageHandler()
    let mutable requests = 0
    member _.Requests = requests
    override _.SendAsync(request: HttpRequestMessage, _: CancellationToken) = task {
        requests <- requests + 1
        if request.RequestUri.Host <> "test.invalid" then failwith "Unexpected host"
        match request.Method.Method, request.RequestUri.AbsolutePath with
        | "GET", "/shared" ->
            let response = new HttpResponseMessage(HttpStatusCode.OK)
            response.Content <- new StringContent("{\"label\":\"from second client\"}")
            return response
        | "POST", "/shared" ->
            let! body = request.Content.ReadAsStringAsync()
            use json = JsonDocument.Parse body
            if json.RootElement.GetProperty("label").GetString() <> "from second client" then
                failwith "Cross-client payload changed"
            return new HttpResponseMessage(HttpStatusCode.NoContent)
        | "DELETE", "/legacy" -> return new HttpResponseMessage(HttpStatusCode.NoContent)
        | method, path -> return failwithf "Unexpected request %s %s" method path
    }

#if TASK
let result (value: Task<'a>) = value.GetAwaiter().GetResult()
#else
let result value = Async.RunSynchronously value
#endif

[<EntryPoint>]
let main _ =
    use handler = new Respond()
    use http = new HttpClient(handler, BaseAddress = Uri "https://test.invalid")
    let first = FirstClient http
    let second = SecondClient http
    let (FetchShared.OK shared) = result (second.FetchShared())
    let (SendShared.NoContent) = result (first.SendShared(shared))
    let (Legacy.NoContent) = result (second.Legacy())
    if handler.Requests <> 3 then failwith "Selected operation coverage changed"
    if typeof<Shared>.Assembly.GetName().Name <> "GroupFixture.Core" then failwith "Shared model has the wrong owner"
    for client in [ typeof<FirstClient>; typeof<SecondClient> ] do
        let generatedDependencies = client.Assembly.GetReferencedAssemblies() |> Array.filter (fun item -> item.Name.StartsWith "GroupFixture.")
        if (generatedDependencies |> Array.map (fun item -> item.Name)) <> [| "GroupFixture.Core" |] then
            failwithf "Unexpected client dependencies: %A" generatedDependencies
    if typeof<FirstClient>.GetMethod("FetchShared") <> null || typeof<SecondClient>.GetMethod("SendShared") <> null then
        failwith "An operation was emitted into both client groups"
    printfn "Shared model, client ownership, and HTTP checks passed"
    0
