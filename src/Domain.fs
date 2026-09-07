[<AutoOpen>]
module Domain

open Fantomas.FCS.Syntax
open Newtonsoft.Json.Linq

[<RequireQualifiedAccess>]
type EmptyDefinitionResolution =
    | Ignore
    | GenerateFreeForm

[<RequireQualifiedAccess>]
/// <summary>Describes the compilation target</summary>
type Target =
    | FSharp
    | Fable

/// <summary>Describes the async return type of the functions of the generated clients</summary>
[<RequireQualifiedAccess>]
type AsyncReturnType =
    | Async
    | Task

[<RequireQualifiedAccess>]
type FactoryFunction =
    | Create
    | None

type ClientGroup = {
    project: string
    operationIds: string list
}

type CodegenConfig = {
    schema: string
    output: string
    target: Target
    project : string
    asyncReturnType: AsyncReturnType
    synchronous: bool
    resolveReferences: bool
    emptyDefinitions: EmptyDefinitionResolution
    overrideSchema: JToken option
    filterTags: string list
    odataSchema: bool
    /// Operation ids whose binary response should be generated as a
    /// `System.IO.Stream` instead of `byte[]`. Only honored by the .NET
    /// (`fsharp`) target; ignored by the Fable target. Default: empty.
    streamingOperations: string list
    /// When true, every generated operation also returns the HTTP response
    /// headers as `(string * string) list`. Default: false.
    responseHeaders: bool
    /// Named union schemas whose JSON payload may be preserved without
    /// asserting membership in one particular alternative. Default: none.
    preserveJsonSchemas: string list
    /// Optional clients with one shared model/HTTP project. Every selected
    /// operation must belong to exactly one group. Default: a single project.
    clientGroups: ClientGroup list
}

type OperationParameter = {
    parameterName: string
    multipartEncoding: string option
    parameterIdent: string
    required: bool
    parameterType: SynType
    docs : string
    location: string
    style: string
    properties: string list
}
