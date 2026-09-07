[<AutoOpen>]
module OperationParameters

open System
open FsAst
open Fantomas.FCS.Syntax
open Microsoft.OpenApi

exception InvalidParameterEncoding of string

let private paramReplace (parameter: string) (sep: char) : string =
    let parts = parameter.Split(sep)
    let firstPart = parts.[0]
    let otherParts = parts.[1..]
    let modified = [
        yield firstPart
        for part in otherParts do
            yield capitalize part
    ]

    modified
    |> List.map (fun part -> part.Replace("$", "").Replace("@", "").Replace(":", ""))
    |> String.concat ""
    |> camelCase

let rec private cleanParamIdent (parameter: string) (parameters: OperationParameter seq) : string =
    if String.IsNullOrWhiteSpace parameter then
        $"p{(parameters |> Seq.length) + 1}"
    else
        let parameter = String(Array.ofSeq (parameter |> Seq.skipWhile (Char.IsLetter >> not)))
        let cleanedParam =
            if parameter.Contains "-" then
                cleanParamIdent (paramReplace parameter '-') parameters
            elif parameter.Contains "_" then
                cleanParamIdent (paramReplace parameter '_') parameters
            elif parameter.Contains "." then
                cleanParamIdent (paramReplace parameter '.') parameters
            elif parameter.Contains "[" || parameter.Contains "]" then
                match parameter.Split([| "["; "]" |], StringSplitOptions.RemoveEmptyEntries) with
                | [| firstPart; secondPart |]  -> cleanParamIdent(camelCase firstPart + capitalize secondPart) parameters
                | _ -> cleanParamIdent(parameter.Replace("[", "").Replace("]", "")) parameters
            else
                camelCase (parameter.Replace("$", "").Replace("@", "").Replace(":", ""))

        if String.IsNullOrWhiteSpace cleanedParam then
            $"p{(parameters |> Seq.length) + 1}"
        else
            cleanedParam

let rec private readParamType (target: Target) (schema: IOpenApiSchema) : SynType =
    if isNull (box schema) then
        if target = Target.FSharp
        then SynType.JToken()
        else SynType.Object()
    else
    match schemaTypeName schema with
    | "integer" when schema.Format = "int64" -> SynType.Int64()
    | "integer" -> SynType.Int()
    | "number" when schema.Format = "float" -> SynType.Float32()
    | "number" ->  SynType.Double()
    | "boolean" -> SynType.Bool()
    | "string" when schema.Format = "uuid" -> SynType.Guid()
    | "string" when schema.Format = "guid" -> SynType.Guid()
    | "string" when schema.Format = "date-time" -> SynType.DateTimeOffset()
    | "string" when schema.Format = "time-span" || schema.Format = "date-span" -> SynType.TimeSpan()
    | "string" when schema.Format = "binary" ->
        if target = Target.FSharp
        then SynType.ByteArray()
        else SynType.Create "File" // from Browser.Types
    | "string" -> SynType.String()
    | "file" ->
        if target = Target.FSharp
        then SynType.ByteArray()
        else SynType.Create "File" // from Browser.Types
    | _ when isSchemaReference schema ->
        // working with a reference type (a $ref to a primitive resolves above)
        let typeName =
            if invalidTitle schema.Title
            then sanitizeTypeName (schemaReferenceId schema)
            else sanitizeTypeName schema.Title
        SynType.Create typeName
    | "array" ->
        let itemsSchema = schema.Items
        if isNull (box itemsSchema) || isEmptySchema itemsSchema then
            // free-form array items (`items: {}` or no item schema)
            (if target = Target.FSharp then SynType.JToken() else SynType.Object()) |> SynType.List
        else
            readParamType target itemsSchema |> SynType.List
    | "object" ->
        if target = Target.FSharp
        then SynType.JObject()
        else SynType.Object()
    | _ ->
        SynType.String()

let private isCancellationToken (parameter: IOpenApiParameter) =
    isNotNull (box parameter.Schema) && schemaReferenceId parameter.Schema = "CancellationToken"

let private processOperationParameters
    (target: Target)
    (parameters: OperationParameter seq)
    (parameter: IOpenApiParameter)
    : OperationParameter option =

    if isNull (box parameter) ||
       not parameter.In.HasValue ||
       (isNull (box parameter.Schema) && (isNull parameter.Content || parameter.Content.Count = 0))
    then None
    else
        let queryEncoding =
            if isNotNull parameter.Extensions && parameter.Extensions.ContainsKey "x-hawaii-query-encoding" then
                match extensionString parameter.Extensions.["x-hawaii-query-encoding"] with
                | Some encoding -> Some encoding
                | None -> raise (InvalidParameterEncoding (sprintf "Query encoding must be a string for parameter %s" parameter.Name))
            else None
        match queryEncoding with
        | Some "dotted-repeat" when target = Target.FSharp && parameter.In.Value = ParameterLocation.Query -> ()
        | Some encoding -> raise (InvalidParameterEncoding (sprintf "Unsupported query encoding %s for parameter %s" encoding parameter.Name))
        | None -> ()
        let jsonQuery = parameter.In.Value = ParameterLocation.Query && isNotNull parameter.Content && parameter.Content.ContainsKey "application/json"
        if jsonQuery && target <> Target.FSharp then
            raise (InvalidParameterEncoding (sprintf "JSON query content currently requires the fsharp target: %s" parameter.Name))
        let shouldSpreadProperties =
            parameter.Style.HasValue
            && parameter.Style.Value = ParameterStyle.DeepObject
            && parameter.Explode
            && isNotNull (box parameter.Schema)
            && schemaTypeName parameter.Schema = "object"

        let properties =
            if shouldSpreadProperties
            then List.ofSeq parameter.Schema.Properties.Keys
            else []

        let paramType =
            if isNotNull parameter.Content && parameter.Content.Count = 1 then
                let firstKey = Seq.head parameter.Content.Keys
                readParamType target parameter.Content.[firstKey].Schema
            elif parameter.In.Value = ParameterLocation.Header && isNotNull (box parameter.Schema) && schemaTypeName parameter.Schema = "object" then
                // edge case for a weird schema
                SynType.String()
            else
                readParamType target parameter.Schema

        let nullable =
            if isNotNull parameter.Extensions && parameter.Extensions.ContainsKey "x-nullable" then
                match extensionBool parameter.Extensions.["x-nullable"] with
                | Some isNullable -> isNullable
                | None -> true
            else
                true

        let parameterIdentifier = cleanParamIdent parameter.Name parameters
        let identifierAlreadyAdded =
            parameters
            |> Seq.exists (fun opParam -> opParam.parameterIdent = parameterIdentifier)

        Some {
            multipartEncoding = None
            parameterName = parameter.Name
            parameterIdent =
                if identifierAlreadyAdded
                then  $"{parameterIdentifier}In{capitalize(string parameter.In.Value)}"
                else parameterIdentifier
            required = parameter.Required || not nullable
            parameterType =  paramType
            docs = parameter.Description
            location =
                if parameter.In.Value = ParameterLocation.Query && isNotNull parameter.Content && parameter.Content.ContainsKey "application/json" then "queryJson"
                elif parameter.In.Value = ParameterLocation.Query && isNotNull parameter.Extensions && parameter.Extensions.ContainsKey "x-hawaii-query-encoding" && extensionString parameter.Extensions.["x-hawaii-query-encoding"] = Some "dotted-repeat" then "queryDotted"
                elif target = Target.FSharp && parameter.In.Value = ParameterLocation.Query && parameter.Style = Nullable ParameterStyle.Form && not parameter.Explode then "queryComma"
                elif target = Target.FSharp && parameter.In.Value = ParameterLocation.Query && schemaTypeName parameter.Schema = "object" && parameter.Style = Nullable ParameterStyle.Form then "queryForm"
                else (string parameter.In.Value).ToLower()
            properties = properties
            style =
                // a synthetic cancellation-token parameter must be detected before
                // the parameter style, which the 3.x model now defaults for query parameters
                if isCancellationToken parameter then "cancellation-token"
                elif parameter.Style.HasValue then (string parameter.Style).ToLower()
                else "none"
        }

// Walk only intersections: selecting a union branch would change the request contract.
let rec private multipartSchemaParts (parents: IOpenApiSchema list) (schema: IOpenApiSchema) =
    let recursive = parents |> List.exists (fun parent ->
        obj.ReferenceEquals(parent, schema)
        || (isSchemaReference schema && isSchemaReference parent && schemaReferenceId parent = schemaReferenceId schema))
    if isNull (box schema) then []
    elif recursive then
        raise (InvalidParameterEncoding "Recursive multipart allOf schemas are not supported")
    elif (isNotNull schema.OneOf && schema.OneOf.Count > 0) || (isNotNull schema.AnyOf && schema.AnyOf.Count > 0) then
        raise (InvalidParameterEncoding "Multipart fields with alternative schemas are not supported")
    else
        schema :: [
            if isNotNull schema.AllOf then
                for child in schema.AllOf do
                    yield! multipartSchemaParts (schema :: parents) child
        ]

let private multipartObjectProperties (schema: IOpenApiSchema) =
    let parts = multipartSchemaParts [] schema
    let fields = System.Collections.Generic.Dictionary<string, ResizeArray<IOpenApiSchema>>()
    let required = System.Collections.Generic.HashSet<string>()
    for part in parts do
        let kind = schemaTypeName part
        if kind <> "object" && not (String.IsNullOrEmpty kind) then
            raise (InvalidParameterEncoding "Multipart request bodies require an object schema")
        if isNotNull part.Properties then
            for property in part.Properties do
                if not (fields.ContainsKey property.Key) then fields.Add(property.Key, ResizeArray())
                fields.[property.Key].Add property.Value
        if isNotNull part.Required then
            for name in part.Required do required.Add name |> ignore
    if fields.Count = 0 then
        raise (InvalidParameterEncoding "Multipart request bodies require declared properties")
    fields |> Seq.map (fun field -> field.Key, List.ofSeq field.Value, required.Contains field.Key) |> Seq.toList

let rec private multipartFieldType target (schemas: IOpenApiSchema list) =
    let parts = schemas |> List.collect (multipartSchemaParts [])
    let concrete = parts |> List.filter (fun schema -> not (String.IsNullOrEmpty (schemaTypeName schema)))
    let kinds = concrete |> List.map schemaTypeName |> List.distinct
    if kinds.Length > 1 then
        raise (InvalidParameterEncoding "Conflicting multipart allOf property types")
    let kind = kinds |> List.tryHead |> Option.defaultValue "object"
    let primary = concrete |> List.tryHead |> Option.defaultWith (fun () -> List.head schemas)
    let declaredType () =
        if concrete.IsEmpty && not (schemas.Length = 1 && isSchemaReference schemas.Head) then
            if target = Target.FSharp then
                if parts |> List.exists (fun schema -> isNotNull schema.Properties && schema.Properties.Count > 0) then SynType.JObject()
                else SynType.JToken()
            else SynType.Object()
        elif schemas.Length = 1 && (isSchemaReference schemas.Head || isNull schemas.Head.AllOf || schemas.Head.AllOf.Count = 0) then
            readParamType target schemas.Head
        elif kind = "object" then
            if target = Target.FSharp then SynType.JObject() else SynType.Object()
        else readParamType target primary
    match kind with
    | "file" | "string" when kind = "file" || (concrete |> List.exists (fun schema -> schema.Format = "binary")) ->
        (if target = Target.FSharp then SynType.Create "MultipartFile" else SynType.Create "File"), "multipartBinary"
    | "array" ->
        let items = concrete |> List.choose (fun schema -> if isNull (box schema.Items) then None else Some schema.Items)
        if items.IsEmpty then
            (if target = Target.FSharp then SynType.JToken() else SynType.Object()) |> SynType.List, "multipartJsonMany"
        else
            let itemType, encoding = multipartFieldType target items
            if encoding = "multipartBinary" then
                if items |> List.collect (multipartSchemaParts []) |> List.exists (fun schema -> schemaTypeName schema = "array") then
                    raise (InvalidParameterEncoding "Nested multipart binary arrays are not supported")
                SynType.List itemType, encoding
            else declaredType (), (if encoding = "multipartScalar" then "multipartScalarMany" else "multipartJsonMany")
    | "object" -> declaredType (), "multipartJson"
    | _ -> declaredType (), "multipartScalar"

// These alternatives choose a multipart representation; they are not disjoint
// JSON union cases. Keep the caller's text/bytes choice explicit.
let private isMultipartTextOrBinary (schema: IOpenApiSchema) =
    isNotNull (box schema)
    && (isNull schema.OneOf || schema.OneOf.Count = 0)
    && (isNull schema.AllOf || schema.AllOf.Count = 0)
    && isNotNull schema.AnyOf && schema.AnyOf.Count = 2
    && (schema.AnyOf |> Seq.forall (fun branch ->
        schemaTypeName branch = "string"
        && (isNull branch.AllOf || branch.AllOf.Count = 0)
        && (isNull branch.AnyOf || branch.AnyOf.Count = 0)
        && (isNull branch.OneOf || branch.OneOf.Count = 0)))
    && (schema.AnyOf |> Seq.exists (fun branch -> String.IsNullOrEmpty branch.Format))
    && (schema.AnyOf |> Seq.exists (fun branch -> branch.Format = "binary"))

let private isJsonPartMedia (contentType: string) =
    let mediaType = contentType.Split(';').[0].Trim()
    mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
    || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)

let private multipartParameters (config: CodegenConfig) (media: IOpenApiMediaType) (parameters: OperationParameter seq) =
    if config.target <> Target.FSharp && isNotNull (box media.Schema) && isNotNull media.Schema.AllOf && media.Schema.AllOf.Count > 0 then
        raise (InvalidParameterEncoding "Composed multipart requests currently require the fsharp target")
    multipartObjectProperties media.Schema
    |> List.fold (fun (result: OperationParameter list) (name, schemas, required) ->
        let encoding =
            if isNotNull media.Encoding && media.Encoding.ContainsKey name then Some media.Encoding.[name]
            else None
        let explicitContentType =
            encoding |> Option.bind (fun value -> if String.IsNullOrWhiteSpace value.ContentType then None else Some value.ContentType)
        let parameterType, location =
            if config.target = Target.FSharp && schemas.Length = 1 && isSchemaReference schemas.Head
               && (explicitContentType |> Option.exists isJsonPartMedia) then
                // A named JSON model has already passed declaration fidelity checks.
                // Do not flatten its union or replace its canonical type with a string.
                readParamType config.target schemas.Head, "multipartJson"
            elif config.target = Target.FSharp && schemas.Length = 1 && isMultipartTextOrBinary schemas.Head then
                SynType.Create "MultipartTextOrBinary", "multipartTextOrBinary"
            else multipartFieldType config.target schemas
        let contentType =
            explicitContentType
            |> Option.defaultValue (if location = "multipartBinary" then "application/octet-stream" elif location.StartsWith("multipartJson", StringComparison.Ordinal) then "application/json" else "text/plain")
        if location <> "multipartBinary" && (contentType.Contains "," || contentType.Contains "*") then
            raise (InvalidParameterEncoding (sprintf "Multipart field %s requires one concrete content type" name))
        let jsonMedia = isJsonPartMedia contentType
        if config.target = Target.FSharp && location.StartsWith("multipartJson", StringComparison.Ordinal) && not jsonMedia then
            raise (InvalidParameterEncoding (sprintf "Structured multipart field %s requires a JSON content type, got %s" name contentType))
        if encoding |> Option.exists (fun value -> isNotNull value.Headers && (value.Headers.Keys |> Seq.exists (fun name -> not (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))))) then
            raise (InvalidParameterEncoding (sprintf "Multipart field %s declares unsupported custom part headers" name))
        let identifier = cleanParamIdent name (Seq.append parameters result)
        let alreadyAdded = Seq.append parameters result |> Seq.exists (fun parameter -> parameter.parameterIdent = identifier)
        result @ [{
            multipartEncoding =
                if config.target <> Target.FSharp || (location = "multipartTextOrBinary" && explicitContentType.IsNone) then None
                else Some contentType
            parameterName = name
            parameterIdent = if alreadyAdded then identifier + "InFormData" else identifier
            required = required
            parameterType = if config.target = Target.FSharp then parameterType else readParamType config.target schemas.Head
            docs = schemas |> List.tryPick (fun schema -> if String.IsNullOrWhiteSpace schema.Description then None else Some schema.Description) |> Option.defaultValue ""
            properties = []
            location = if config.target = Target.FSharp then (if jsonMedia && location <> "multipartBinary" then (if location.EndsWith("Many", StringComparison.Ordinal) then "multipartJsonMany" else "multipartJson") else location) else "multipartFormData"
            style = "formfield"
        }]) []

let private processOperationRequestBody
    (config: CodegenConfig)
    (operation: OpenApiOperation)
    (parameters: OperationParameter seq)
    : OperationParameter list =

    if isNull (box operation.RequestBody) then []
    else
        let content = operation.RequestBody.Content
        let jsonKey = tryJsonMediaType content
        let bodyIdent =
            if isNotNull operation.RequestBody.Extensions && operation.RequestBody.Extensions.ContainsKey "x-bodyName" then
                extensionString operation.RequestBody.Extensions.["x-bodyName"] |> Option.defaultValue "body"
            else "body"
        let jsonLocation =
            match jsonKey with
            | Some "application/json" | None -> "jsonContent"
            | Some _ when config.target = Target.FSharp -> "jsonMediaContent"
            | Some media -> raise (InvalidParameterEncoding (sprintf "Structured JSON media %s currently requires the fsharp target" media))

        if (hasJsonMediaType content) && not (isEmptySchema (jsonMedia content).Schema) then
            let schema = (jsonMedia content).Schema
            let typeName = "body"
            let parameterName =
                if operation.RequestBody.Extensions.ContainsKey "x-bodyName" then
                    match extensionString operation.RequestBody.Extensions.["x-bodyName"] with
                    | Some name -> name
                    | None -> camelCase (normalizeFullCaps typeName)
                else
                    camelCase (normalizeFullCaps typeName)

            let requestTypePayload =
                if operation.Extensions.ContainsKey "RequestTypePayload" then
                    match extensionString operation.Extensions.["RequestTypePayload"] with
                    | Some requestTypePayload ->
                        SynType.Create requestTypePayload
                    | None ->
                        readParamType config.target schema
                else
                    readParamType config.target schema

            [{
                multipartEncoding = None
                parameterName = if jsonLocation = "jsonContent" then parameterName else jsonKey.Value
                parameterIdent = cleanParamIdent parameterName parameters
                required = operation.RequestBody.Required
                parameterType = requestTypePayload
                docs = schema.Description
                location = jsonLocation
                style = "none"
                properties = []
            }]

        elif (hasJsonMediaType content) && isEmptySchema (jsonMedia content).Schema && config.emptyDefinitions = EmptyDefinitionResolution.GenerateFreeForm then
            let schema = (jsonMedia content).Schema
            let typeName = "body"
            let parameterName =
                if operation.RequestBody.Extensions.ContainsKey "x-bodyName" then
                    match extensionString operation.RequestBody.Extensions.["x-bodyName"] with
                    | Some name -> name
                    | None -> camelCase (normalizeFullCaps typeName)
                else
                    camelCase (normalizeFullCaps typeName)

            [{
                multipartEncoding = None
                parameterName = if jsonLocation = "jsonContent" then parameterName else jsonKey.Value
                parameterIdent = cleanParamIdent parameterName parameters
                required = operation.RequestBody.Required
                parameterType =
                    if config.target = Target.FSharp
                    then SynType.JToken()
                    else SynType.Object()
                docs =
                    if isNotNull (box schema)
                    then schema.Description
                    else ""
                location = jsonLocation
                style = "none"
                properties = []
            }]

        elif content.ContainsKey "multipart/form-data" then
            multipartParameters config content.["multipart/form-data"] parameters

        elif content.ContainsKey "application/x-www-form-urlencoded" then
            let schema = content.["application/x-www-form-urlencoded"].Schema
            if isNull (box schema) || schemaTypeName schema <> "object" then
                raise (InvalidParameterEncoding (sprintf "Form URL encoding requires an object schema for operation %s" operation.OperationId))
            else
                schema.Properties
                |> Seq.map (fun property ->
                    {
                        multipartEncoding = None
                        parameterName = property.Key
                        parameterIdent = cleanParamIdent property.Key parameters
                        required = schema.Required.Contains property.Key
                        parameterType = readParamType config.target property.Value
                        docs = property.Value.Description
                        properties = []
                        location = "urlEncodedFormData"
                        style = "formfield"
                    })
                |> Seq.toList

        elif config.target = Target.FSharp && content.Count = 1 then
            let media = Seq.head content.Keys
            let schema = content.[media].Schema
            let baseMedia = media.Split(';').[0].Trim()
            let binary = baseMedia = "application/octet-stream" || (isNotNull (box schema) && schema.Format = "binary")
            let text = baseMedia.StartsWith("text/", StringComparison.OrdinalIgnoreCase) && schemaTypeName schema = "string"
            if not binary && not text then
                raise (InvalidParameterEncoding (sprintf "Unsupported request body encoding %s for operation %s" media operation.OperationId))
            [{
                multipartEncoding = None
                parameterName = media
                parameterIdent = cleanParamIdent bodyIdent parameters
                required = operation.RequestBody.Required
                parameterType = if binary then SynType.ByteArray() else SynType.String()
                docs = if isNotNull (box schema) then schema.Description else ""
                location = if binary then "rawContent" else "textContent"
                style = "none"
                properties = []
            }]
        elif content.ContainsKey "application/octet-stream" && config.target = Target.Fable then
            [{ multipartEncoding = None; parameterName = "requestBody"; parameterIdent = "requestBody"; required = operation.RequestBody.Required
               parameterType = SynType.ByteArray(); docs = ""; location = "binaryContent"; style = "formfield"; properties = [] }]
        else
            raise (InvalidParameterEncoding (sprintf "Unsupported request body media for operation %s: %s" operation.OperationId (String.concat ", " content.Keys)))

let operationParameters
    (operation: OpenApiOperation)
    (pathInfoParameters: IOpenApiParameter seq)
    (config: CodegenConfig)
    : OperationParameter list =

    let opParameters =
        (Seq.append pathInfoParameters operation.Parameters)
        |> Seq.fold (fun opParameters oaParameter ->
            match (processOperationParameters config.target opParameters oaParameter) with
            | None -> opParameters
            | Some opParameter -> opParameter :: opParameters) []
        |> List.rev

    let rqParameters =
        processOperationRequestBody config operation opParameters
        |> List.rev

    opParameters @ rqParameters
    |> List.sortBy (fun param ->
        // required parameters come first
        if param.required
        then 0
        else 1
    )
