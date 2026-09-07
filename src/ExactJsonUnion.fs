module ExactJsonUnion

open System
open System.Collections.Generic
open Microsoft.OpenApi
open Helpers

type Selection =
    | Kind of string list
    | RequiredTag of string * string list

type Branch = { Schema: IOpenApiSchema; Selection: Selection }

let private sequence (values: seq<'a>) = if isNull values then Seq.empty else values

// This determines an unambiguous wire dispatch, not a full schema validator.
// In particular, different required property names do not make objects disjoint.
let tryBranches (schema: IOpenApiSchema) =
    let branches =
        if Seq.isEmpty (sequence schema.OneOf) then sequence schema.AnyOf |> Seq.toList
        else sequence schema.OneOf |> Seq.toList
    let rec properties (seen: HashSet<IOpenApiSchema>) (part: IOpenApiSchema) =
        if not (seen.Add part) then Map.empty, Set.empty
        else
            let mutable fields = sequence part.Properties |> Seq.map (fun p -> p.Key, [p.Value]) |> Map.ofSeq
            let mutable required = sequence part.Required |> Set.ofSeq
            for parent in sequence part.AllOf do
                let inherited, inheritedRequired = properties seen parent
                fields <- Map.fold (fun fields name values -> Map.add name ((Map.tryFind name fields |> Option.defaultValue []) @ values) fields) fields inherited
                required <- Set.union required inheritedRequired
            fields, required
    let tags part =
        let fields, required = properties (HashSet<IOpenApiSchema>(HashIdentity.Reference)) part
        let rec requiresObject (seen: HashSet<IOpenApiSchema>) schema =
            seen.Add schema &&
            ((schemaTypeName schema = "object" && not (schemaIsNullable schema)) || sequence schema.AllOf |> Seq.exists (requiresObject seen))
        let isObject = requiresObject (HashSet<IOpenApiSchema>(HashIdentity.Reference)) part
        required
        |> Seq.choose (fun name ->
            match Map.tryFind name fields with
            | Some constraints ->
                let valueSets = constraints |> List.choose (fun field ->
                    let values = sequence field.Enum |> Seq.choose nodeAsString |> Seq.toList
                    if not values.IsEmpty && values.Length = Seq.length (sequence field.Enum) then Some(Set.ofList values) else None)
                let values =
                    match valueSets with
                    | first :: rest -> rest |> List.fold Set.intersect first |> Set.toList
                    | [] -> []
                // Only finite string tags are admitted here. Boolean and numeric
                // tags need their own semantic equality and representation checks.
                if isObject && not (schemaIsNullable part) && not values.IsEmpty then Some(name, values) else None
            | None -> None)
        |> Map.ofSeq
    let tagSets = branches |> List.map tags
    let disjoint sets =
        sets |> List.forall (fun values -> not (Set.isEmpty values)) &&
        (sets |> List.sumBy Set.count) = (sets |> List.fold Set.union Set.empty |> Set.count)
    let tag =
        match tagSets with
        | first :: rest ->
            first |> Map.toSeq |> Seq.tryPick (fun (name, _) ->
                let values = tagSets |> List.map (Map.tryFind name)
                if values |> List.forall Option.isSome then
                    let values = values |> List.map Option.get
                    if disjoint (values |> List.map Set.ofList) then Some(name, values) else None
                else None)
        | [] -> None
    if branches.Length < 2 then None
    else
        match tag with
        | Some(name, values) -> Some(List.map2 (fun part values -> { Schema = part; Selection = RequiredTag(name, values) }) branches values)
        | None ->
            let kinds = branches |> List.map (fun part ->
                let kind = match schemaTypeName part with "integer" -> "Number" | "number" -> "Number" | "boolean" -> "Boolean" | "string" -> "String" | "object" -> "Object" | "array" -> "Array" | _ -> ""
                if kind = "" && schemaTypeFlags part <> Nullable(JsonSchemaType.Null) then []
                else [ if kind <> "" then kind; if schemaIsNullable part then "Null" ])
            if disjoint (kinds |> List.map Set.ofList) then
                Some(List.map2 (fun part kinds -> { Schema = part; Selection = Kind kinds }) branches kinds)
            else None

let private literal (value: string) = System.Text.Json.JsonSerializer.Serialize value

let private condition variable = function
    | Kind kinds ->
        kinds |> List.map (function
            | "Boolean" -> $"({variable}.ValueKind = System.Text.Json.JsonValueKind.True || {variable}.ValueKind = System.Text.Json.JsonValueKind.False)"
            | kind -> $"{variable}.ValueKind = System.Text.Json.JsonValueKind.{kind}") |> String.concat " || "
    | RequiredTag(name, values) ->
        let choices = values |> List.map (fun value -> "tag.GetString() = " + literal value) |> String.concat " || "
        $"({variable}.ValueKind = System.Text.Json.JsonValueKind.Object && (let mutable tag = Unchecked.defaultof<System.Text.Json.JsonElement> in {variable}.TryGetProperty({literal name}, &tag) && tag.ValueKind = System.Text.Json.JsonValueKind.String && ({choices})))"

/// Payload types retain their own tag fields; the converter writes the selected
/// payload once, so standalone members and union members share the same schema.
let declaration serializer name (branches: (string * string * Selection) list) =
    let output = System.Text.StringBuilder()
    let line (value: string) = output.AppendLine(value) |> ignore
    line ""
    line $"[<RequireQualifiedAccess; System.Text.Json.Serialization.JsonConverter(typeof<{name}_JsonConverter>)>]"
    line $"type {name} ="
    for caseName, payload, _ in branches do line $"    | {caseName} of {payload}"
    line "    member this.Format() ="
    line $"        use document = System.Text.Json.JsonDocument.Parse({serializer}.serialize this)"
    line "        match document.RootElement.ValueKind with"
    line "        | System.Text.Json.JsonValueKind.String -> document.RootElement.GetString()"
    line "        | System.Text.Json.JsonValueKind.Number"
    line "        | System.Text.Json.JsonValueKind.True"
    line "        | System.Text.Json.JsonValueKind.False -> document.RootElement.GetRawText()"
    line "        | _ -> invalidOp \"A scalar JSON value is required for this URL parameter\""
    line $"and private {name}_JsonConverter() ="
    line $"    inherit System.Text.Json.Serialization.JsonConverter<{name}>()"
    line "    override _.HandleNull = true"
    line "    override _.Read(reader: byref<System.Text.Json.Utf8JsonReader>, _: System.Type, options: System.Text.Json.JsonSerializerOptions) ="
    line "        use document = System.Text.Json.JsonDocument.ParseValue(&reader)"
    line "        let value = document.RootElement"
    for index, (caseName, payload, selection) in List.indexed branches do
        let keyword = if index = 0 then "if" else "elif"
        line $"""        {keyword} {condition "value" selection} then {name}.{caseName}(System.Text.Json.JsonSerializer.Deserialize<{payload}>(value.GetRawText(), options))"""
    line $"""        else raise (System.Text.Json.JsonException({literal ("No declared wire branch matches " + name)}))"""
    line $"    override _.Write(writer: System.Text.Json.Utf8JsonWriter, value: {name}, options: System.Text.Json.JsonSerializerOptions) ="
    line "        match value with"
    for caseName, payload, selection in branches do
        line $"        | {name}.{caseName} payload ->"
        line $"            use document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize<{payload}>(payload, options))"
        line "            let value = document.RootElement"
        line $"""            if not ({condition "value" selection}) then raise (System.Text.Json.JsonException({literal ("Payload does not match case " + name + "." + caseName)}))"""
        line "            value.WriteTo(writer)"
    output.ToString()
