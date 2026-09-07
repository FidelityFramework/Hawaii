module InlineSchemas

open System
open System.Collections.Generic
open System.Text
open Newtonsoft.Json.Linq

type Site = { SourcePointer: string; Name: string option; Handling: string }
exception InvalidPromotion of string

let private pointer parts = "#/" + (parts |> List.map (fun (part: string) -> part.Replace("~", "~0").Replace("/", "~1")) |> String.concat "/")

/// Give otherwise lossy inline unions a stable declaration owner. Existing
/// unconstrained JSON fields and primitive representations keep their paths.
/// The caller still applies ordinary exact-dispatch / explicit-preservation
/// policy to every promoted name; promotion does not choose an alternative.
let promote (source: JObject) (preservedNames: Set<string>) =
    let result = source.DeepClone() :?> JObject
    if isNull result.["components"] then result.["components"] <- JObject()
    let components = result.["components"] :?> JObject
    if isNull components.["schemas"] then components.["schemas"] <- JObject()
    let definitions = components.["schemas"] :?> JObject
    let sites = ResizeArray<Site>()
    let names = Dictionary<string, string>()
    let isUnion (schema: JObject) =
        [ "oneOf"; "anyOf" ] |> List.exists (fun key -> match schema.[key] with :? JArray as values -> values.Count > 1 | _ -> false)
    let rec containsUnion seen (schema: JObject) =
        match schema.["$ref"] with
        | :? JValue as reference when reference.ToString().StartsWith("#/components/schemas/") ->
            let name = reference.ToString().Substring("#/components/schemas/".Length).Replace("~1", "/").Replace("~0", "~")
            if Set.contains name seen then false
            else
                match definitions.[name] with
                | :? JObject as target -> containsUnion (Set.add name seen) target
                | _ -> false
        | _ ->
            isUnion schema ||
            (match schema.["allOf"] with
             | :? JArray as parts -> parts |> Seq.exists (function :? JObject as part -> containsUnion seen part | _ -> false)
             | _ -> false)
    let composedUnion (schema: JObject) =
        match schema.["allOf"] with
        | :? JArray as parts ->
            // Match already-supported reference/annotation aliases before
            // assigning a new owner. Generator provenance is only annotation.
            let references = parts |> Seq.choose (function :? JObject as p when not(isNull p.["$ref"]) -> Some p | _ -> None) |> Seq.toList
            let annotationAlias =
                match references with
                | [target] ->
                    let refName = target.["$ref"].ToString().Split('/') |> Array.last |> fun name -> name.Replace("~1", "/").Replace("~0", "~")
                    let targetType = if definitions.ContainsKey refName then definitions.[refName].["type"] else null
                    let compatible allowAllOf (part: JObject) =
                        part.Properties() |> Seq.forall (fun p ->
                            if [ "description"; "title"; "example"; "examples"; "readOnly"; "writeOnly"; "deprecated"; "default"; "x-hawaii-source-pointer"; "x-hawaii-original-title" ] |> List.contains p.Name then true
                            elif p.Name = "allOf" then allowAllOf
                            elif p.Name = "type" then JToken.DeepEquals(p.Value, targetType)
                            else false)
                    compatible true schema && (parts |> Seq.forall (function :? JObject as part -> obj.ReferenceEquals(part,target) || compatible false part | _ -> false))
                | _ -> false
            not annotationAlias && (parts |> Seq.exists (function :? JObject as part -> containsUnion Set.empty part | _ -> false))
        | _ -> false
    let rec visit path position (schema: JObject) =
        let sourcePointer = pointer path
        let named = position = "named"
        let existingName = if named then path |> List.last else ""
        if named && preservedNames.Contains existingName then ()
        elif not (isNull schema.["$ref"]) then ()
        elif not named && (isUnion schema || composedUnion schema) then
            let plainJsonField =
                position = "field" && isNull schema.["type"] && isNull schema.["enum"] && isNull schema.["additionalProperties"]
            let primitive =
                match schema.["type"] with
                | null -> false
                | value -> [ "string"; "integer"; "number"; "boolean" ] |> List.contains (value.ToString())
            if plainJsonField || primitive then
                sites.Add { SourcePointer = sourcePointer; Name = None; Handling = if plainJsonField then "existing-json-value" else "existing-primitive" }
            else
                let digest = Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes sourcePointer) |> Convert.ToHexString |> fun text -> text.ToLowerInvariant()
                let name = "InlineUnion_" + digest.Substring(0, 24)
                if definitions.ContainsKey name || names.ContainsKey name then
                    raise (InvalidPromotion (sprintf "Inline schema name collision at %s (%s)" sourcePointer name))
                names.Add(name, sourcePointer)
                let definition = schema.DeepClone() :?> JObject
                // Title is only an annotation; retain it separately while
                // making Hawaii's existing title-based naming deterministic.
                if not (isNull definition.["title"]) then definition.["x-hawaii-original-title"] <- definition.["title"].DeepClone()
                definition.["title"] <- JValue name
                definition.["x-hawaii-source-pointer"] <- JValue sourcePointer
                definitions.Add(name, definition)
                sites.Add { SourcePointer = sourcePointer; Name = Some name; Handling = "promoted" }
                schema.RemoveAll()
                schema.Add("$ref", JValue("#/components/schemas/" + name))
                if not (preservedNames.Contains name) then descend path definition
        else descend path schema
    and descend path (schema: JObject) =
        for key in [ "properties"; "patternProperties"; "$defs" ] do
            match schema.[key] with
            | :? JObject as fields ->
                for field in fields.Properties() |> Seq.toList do
                    match field.Value with
                    | :? JObject as child -> visit (path @ [ key; field.Name ]) (if key = "properties" then "field" else "value") child
                    | _ -> ()
            | _ -> ()
        for key in [ "items"; "additionalProperties"; "not"; "contains"; "if"; "then"; "else" ] do
            match schema.[key] with
            | :? JObject as child -> visit (path @ [key]) "value" child
            | _ -> ()
        for key in [ "oneOf"; "anyOf"; "allOf"; "prefixItems" ] do
            match schema.[key] with
            | :? JArray as children ->
                for index, child in children |> Seq.indexed do
                    match child with
                    | :? JObject as child -> visit (path @ [ key; string index ]) "value" child
                    | _ -> ()
            | _ -> ()
    for definition in definitions.Properties() |> Seq.toList do
        match definition.Value with
        | :? JObject as schema -> visit [ "components"; "schemas"; definition.Name ] "named" schema
        | _ -> ()
    let rec findSchemas path (node: JToken) =
        match node with
        | :? JObject as fields ->
            for field in fields.Properties() |> Seq.toList do
                if field.Name = "schema" then
                    match field.Value with
                    | :? JObject as schema -> visit (path @ [field.Name]) "value" schema
                    | _ -> ()
                elif field.Name <> "example" && field.Name <> "examples" then findSchemas (path @ [field.Name]) field.Value
        | :? JArray as values -> for index, value in values |> Seq.indexed do findSchemas (path @ [string index]) value
        | _ -> ()
    if not (isNull result.["paths"]) then findSchemas ["paths"] result.["paths"]
    if not (isNull result.["webhooks"]) then findSchemas ["webhooks"] result.["webhooks"]
    for entry in components.Properties() |> Seq.toList do
        if entry.Name <> "schemas" then findSchemas ["components"; entry.Name] entry.Value
    result, Seq.toList sites
