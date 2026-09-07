module Hawaii.ClientEmission.Tests

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Hawaii.ClientEmission
open Hawaii.ClientEmission.Types

type Respond(status: int, expectedPath: string) =
    inherit HttpMessageHandler()
    override _.SendAsync(request: HttpRequestMessage, _: CancellationToken) =
        if request.RequestUri.Host <> "test.invalid" || request.RequestUri.PathAndQuery <> expectedPath then
            failwithf "Unexpected request URI: %O" request.RequestUri
        let response = new HttpResponseMessage(enum<HttpStatusCode> status)
        response.Content <- new StringContent("")
        Task.FromResult response

let mutable passed = 0
let check condition =
    if not condition then failwith "Unexpected generated response case"
    passed <- passed + 1

#if TASK
let result (value: Task<'a>) = value.GetAwaiter().GetResult()
#else
let result value = Async.RunSynchronously value
#endif

let respond code expectedPath action =
    use handler = new Respond(code, expectedPath)
    use http = new HttpClient(handler, BaseAddress = Uri "https://test.invalid")
    action (ClientEmissionClient http)

[<EntryPoint>]
let main _ =
    respond 204 "/jobs/sample" (fun client -> check (result (client.FetchJob(task = "sample")) = FetchJob.NoContent))
    respond 204 "/async?async=enabled" (fun client -> check (result (client.FetchAsync(``async`` = "enabled")) = FetchAsync.NoContent))
    respond 204 "/empty" (fun client -> check (result (client.Empty()) = Empty.NoContent))
    for code in [ 400; 499 ] do
        respond code "/empty" (fun client -> check (result (client.Empty()) = Empty.Status4XX code))
    for code in [ 302; 503 ] do
        respond code "/empty" (fun client -> check (result (client.Empty()) = Empty.DefaultResponse code))
    respond 204 "/legacy" (fun client -> check (result (client.Legacy()) = Legacy.NoContent))
    printfn "%d client emission checks passed" passed
    0
