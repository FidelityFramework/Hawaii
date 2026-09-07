# Hawaii.Unofficial  [![Nuget](https://img.shields.io/nuget/v/Hawaii.Unofficial.svg?colorB=green)](https://www.nuget.org/packages/Hawaii.Unofficial)

![](hawaii.png)

A dotnet CLI tool to generate type-safe F# and Fable clients from OpenAPI/Swagger/OData services.

This branch builds on [Onur Gumus's modernization of Hawaii](https://github.com/OnurGumus/Hawaii/tree/modernization),
which continues Zaid Ajaj's original project. The upstream base is commit
`7899d3504ec2fa2e07e2790e02ef5829316cc1e6`, published as `Hawaii.Unofficial` 1.0.0.
The `fsharp-cloudedge-support` branch adds generic schema, HTTP-contract, and
shared-model client-group corrections used by FSharp.CloudEdge. Those local
changes require a source build; installing the upstream NuGet package does not
install this branch's additions.

The generator runs on .NET 10 and uses OpenAPI.NET to read OpenAPI 2.0, 3.0,
3.1, and 3.2 inputs. Generated .NET clients use System.Text.Json; the separate
Fable target uses Thoth.Json. Reader support for a document version does not
imply that every schema construct has a faithful generated representation.
See the [branch contracts and explicit fidelity policy](docs/fsharp-cloudedge-support.md)
[client groups with one shared model](docs/client-groups.md), and the
[multipart request contract](docs/multipart-request-contract.md).

### Features
 - Reads JSON and YAML OpenAPI/Swagger inputs through [OpenAPI.NET](https://github.com/microsoft/OpenAPI.NET); unsupported representations produce diagnostics.
 - Supports [OData](https://www.odata.org) services (see example below), made possible by [OpenAPI.NET.OData](https://github.com/Microsoft/OpenAPI.NET.OData) which translates the OData model into an OpenApi document
 - Generates clients for F# on dotnet or for Fable in the browser
 - Automatically handles JSON deserialization into schema types
 - Automatically handles mixed responses from end points that return binary _or_ typed JSON responses
 - Generates discriminated union types to describe the possible responses of each endpoint
 - Generates full F# projects, including their required dependencies and targeting `netstandard2.0`

### Package and use this branch locally

Build the tool package from this checkout, then install it into the consuming
repository's local tool manifest. Choose a distinct local version; do not reuse
a published version for different source.

```sh
# In the Hawaii checkout:
dotnet pack src/Hawaii.fsproj -c Release -o /path/to/local-feed -p:Version="<local-version>"

# In the consuming repository (create a manifest only if it has none):
dotnet new tool-manifest
dotnet tool install --local Hawaii.Unofficial --version "<local-version>" --source /path/to/local-feed
dotnet tool run hawaii-unofficial -- --config /path/to/hawaii.json
```

FSharp.CloudEdge maintains its own bootstrap, feed, and pinned tool manifest.
Its ordinary generation commands consume the installed tool, separately from
this source checkout. The upstream published release is available as
`Hawaii.Unofficial`; it does not include unpublished changes from this branch.

For generator development and the regression harnesses below, build with
`dotnet build src/Hawaii.fsproj`; those tests can also accept a frozen assembly
through `--assembly`.

### Configuration

Create a configuration file called `hawaii.json` with the following shape:
```
{
    "schema": <schema>,
    "project": <project>,
    "output": <output>,
    ["target"]: <"fsharp" | "fable">
    ["synchronous"]: <true | false>,
    ["asyncReturnType"]: <"async" | "task">,
    ["resolveReferences"]: <true | false>,
    ["emptyDefinitions"]: <"ignore" | "free-form">,
    ["responseHeaders"]: <true | false>,
    ["streamingOperations"]: <list of operation ids>,
    ["overrideSchema"]: <JSON schema subset>,
    ["filterTags"]: <list of tags>,
    ["preserveJsonSchemas"]: <list of exact schema ids>,
    ["clientGroups"]: <list of { "project": <name>, "operationIds": <list of ids> }>
}
```
Where
 - `<schema>` is a URL to the OpenApi/Swagger/OData location, whether that is an external URL or a relative file path. In case of OData services, the external URL has to end with `$metadata` which points to the Edm model of that service (see TripPinService example below) or it can be a local `.xml` file that contains the schema
 - `<project>` is the name of the project that will get generated
 - `<output>` is a relative path to the output directory where the project will be generated. (The output directory is replaced after schema validation and client rendering succeed; input diagnostics leave existing output intact.)
 - `<synchronous>` is an optional flag that determines whether hawaii should generate client methods that run http requests synchronously. This is useful when used inside console applications. (set to false by default)
 - `<target>` specifies whether hawaii should generate a client for F# on dotnet  (default) or a Fable client
 - `<asyncReturnType>` is an option to determine whether hawaii should generate client methods that return `Async<'T>` when set to "async" (default) or `Task<'T>` when set to "task" (this option is irrelevant when the `synchronous` option is set to `true`)
 - `<resolveReferences>` determines whether hawaii will attempt to resolve external references via schema pre-processing. This is set to `false` by default but sometimes an OpenApi schema is scattered into multiple schemas across a repository and this might help with the resolution.
 - `<emptyDefintions>` determines what hawaii should do when encountering a global type definition without schema information. When set to "ignore" (default), Hawaii omits the empty global type; a remaining reference to a missing declaration is diagnosed. However, sometimes these global types are still referenced from other types or definitions, in which case the setting this option to "free-form" will generate a type abbreviation for the empty schema equal to a free form object (`System.Text.Json.Nodes.JsonNode` when targetting F# or `obj` when targetting Fable)
 - `<responseHeaders>` when set to `true`, every generated operation also returns the HTTP response headers as a `(string * string) list` alongside the response value. Defaults to `false`, leaving the generated client unchanged.
 - `<streamingOperations>` an optional list of operation ids whose binary (`application/octet-stream`) responses should be consumed as a `System.IO.Stream` instead of `byte[]` - useful for large downloads. Applies to the F# target only.
 - `<overrideSchema>` Allows you to override the resolved schema either to add more information (such as a missing operation ID) or _correct_ the types when you know better (see below)
 - `<filterTags>` Allows to filter which operations will be included based on their OpenAPI tags. Useful when generating the full schema isn't possible or isn't practical. To see what tags are available, use `hawaii-unofficial --show-tags`

`preserveJsonSchemas` is a .NET-only, explicit opt-in for unresolved union and
composed-union representations. Each listed source ID retains a distinct
JSON-backed type and an entry in `schema-fidelity.json`; it does not gain a
proof of union membership or intersection validity. The default is a diagnostic.
`clientGroups` assigns every selected operation to exactly one client project
while keeping schema and HTTP definitions in one shared project. See the linked
contract documents for the complete rules and examples.

### Example ([PetStore](https://petstore3.swagger.io) Schema)
Here is an example configuration for the pet store API:
```json
{
    "schema": "https://petstore3.swagger.io/api/v3/openapi.json",
    "output": "./output",
    "project": "PetStore",
    "synchronous": true
}
```
After you have the configuration ready, run `hawaii-unofficial` in the directory where you have the `hawaii.json` file:
```
hawaii-unofficial
```
You can also tell hawaii where to find the configuration file if it wasn't named `hawaii.json`, for example
```
hawaii-unofficial --config ./petstore-hawaii.json
```
### Using the generated project
Once hawaii has finished running, you find a fully generated F# project inside of the `<output>` directory. This project can be referenced from your application so you can start using it.

You can reference the project like this from your app like this:
```xml
<ItemGroup>
  <ProjectReference Include="..\path\to\output\PetStore.fsproj" />
</ItemGroup>
```
Then from your code:
```fs
open System
open System.Net.Http
open PetStore
open PetStore.Types

let petStoreUri = Uri "https://petstore3.swagger.io/api/v3"
let httpClient = new HttpClient(BaseAddress=petStoreUri)
let petStore = PetStoreClient(httpClient)

let availablePets() =
    let status = PetStatus.Available.Format()
    match petStore.FindPetsByStatus(status) with
    | FindPetsByStatus.OK pets -> for pet in pets do printfn $"{pet.name}"
    | FindPetsByStatus.BadRequest -> printfn "Bad request"

availablePets()

// inventory : Map<string, int>
let (GetInventory.OK(inventory)) = petStore.GetInventory()

for (status, quantity) in Map.toList inventory do
    printfn $"There are {quantity} pet(s) {status}"
```
Notice that you have to provide your own `HttpClient` to the `PetStoreClient` and set the `BaseAddress` to the base path of the service.

### Example with OData ([TripPinService](https://services.odata.org/v4/TripPinServiceRW) schema)
```json
{
  "schema": "https://services.odata.org/V4/(S(s3lb035ptje4a1j0bvkmqqa0))/TripPinServiceRW/$metadata",
  "project": "TripPinService",
  "output": "./output"
}
```

### Generate OpenAPI specs from the OData schemas

Sometimes you want to see how Hawaii generated a client from an OData schema. 

You use the following command to generate the intermediate OpenAPI specs file in the form of JSON.

Then you can inspect it but also modify then use it as your `<schema>` when you need to make corrections to the generated client

```
hawaii-unofficial --from-odata-schema {schema} --output {output}
```
where
  - `{schema}` is either a URL to an external OData schema or local .xml file which the contents of the schema
  - `{output}` is a relative file path where the translated OpenAPI specs will be written

Example of such command
```
hawaii-unofficial --from-odata-schema "https://services.odata.org/V4/(S(s3lb035ptje4a1j0bvkmqqa0))/TripPinServiceRW/$metadata" --output ./TripPin.json
```
### Sample Applications

- [Minimal PetStore](https://github.com/Zaid-Ajaj/hawaii-samples-petstore) - A simple console application showing how to use the PetStore API client generated by Hawaii
- [Feliz PetStore](https://github.com/Zaid-Ajaj/hawaii-samples-feliz-petstore) - A [Feliz](https://github.com/Zaid-Ajaj/Feliz) application that shows how to use the PetStore API client generated by Hawaii when targeting Fable
- [OData TripPin Service](https://github.com/Zaid-Ajaj/hawaii-samples-odata-trippin) - This application demonstrates a console application that uses a client for the [TripPin](https://www.odata.org/blog/trippin-new-odata-v4-sample-service) OData service generated by [Hawaii](https://github.com/Zaid-Ajaj/Hawaii)

### Version
You can ask hawaii which version it is currently on:
```
hawaii-unofficial --version
```

### No Logo
If you don't want the logo to show up in your CI or local machine, add `--no-logo` as the last parameter
```
hawaii-unofficial --no-logo
hawaii-unofficial --config ./hawaii.json --no-logo
```

### Advanced - Overriding The Schema
OpenAPI schemas can be very loose and not always typed. Sometimes they will be missing operation IDs on certain paths. Although Hawaii will attempt to derive valid operation IDs from the path, name collisions can sometimes happen.
Hawaii provides the `overrideSchema` option to allow you to "fix" the source schema or add more information when its missing.

Here is an example for how you can override operation IDs for certain paths
```json
{
  "overrideSchema": {
    "paths": {
      "/consumer/v1/services/{id}/allocations": {
        "get": {
          "operationId": "getAllocationsForCustomerByServiceId"
        }
      },
      "/consumer/v1/services/allocations/{id}": {
        "get": {
            "operationId": "getAllocationIdFromCustomerServices"
        }
      }
    }
  }
}
```
The `overrideSchema` property basically takes a subset of another schema and _merges_ it with the source schema.

You can go a step further by overriding the return types of certain responses. The following example shows how you can get the raw text output from the default response of a path instead of getting a typed response:
```json
{
  "overrideSchema": {
    "paths": {
      "/bin/querybuilder.json": {
        "get": {
          "responses": {
            "default": {
              "content": {
                "application/json": {
                  "schema": {
                    "type": "string"
                  }
                }
              }
            }
          }
        }
      }
    }
  }
}
```

### Representation boundaries

Disjoint JSON kinds and required, disjoint literal tags support typed union
wire dispatch. A discriminator mapping alone does not establish exclusivity.
Single-member compositions are simplified only when sibling constraints remain
compatible. Other named unions, including intersections containing a union,
require an explicit preservation policy or produce a diagnostic. The emitted
converters are not complete JSON Schema validators.

The .NET and Fable targets have separate runtime support. Shared client groups
and preserved-JSON wrappers currently target .NET. Whole-provider generation,
compilation, and outgoing-request checks remain consumer acceptance gates;
no deployed Cloudflare behavior or complete provider coverage is implied by a
successful narrow test. Cloudflare lifecycle and taxonomy policy stays in
FSharp.CloudEdge, outside this generic generator.

### Branch regression gates

```sh
python3 tests/response-contracts/run.py
python3 tests/client-emission/run.py
python3 tests/client-groups/run.py
python3 tests/multipart-requests/run.py
```

These gates generate fresh clients, compile them in Debug and Release, and
exercise their contracts through in-process HTTP handlers. They do not send
requests to provider accounts. The historical integration harness below is
separate from these focused branch regressions.

> You can watch the live coding sessions as a playlist published on [YouTube here](https://www.youtube.com/watch?v=8dgjD6vG7yw&list=PLBzGkJMamtz0KCkK7OFnuXyXP7yUtnt9o)

### Running integration tests
```bash
cd ./build
# run hawaii against multiple config permutations
dotnet run -- generate-and-build
# run hawaii against 10 schemas
dotnet run -- integration
# run hawaii agains the first {n} schemas out of ~2000 and see the progress
dotnet run -- rate {n}
```
