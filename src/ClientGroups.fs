module ClientGroups

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open Newtonsoft.Json.Linq

exception InvalidClientGroups of string

let private invalid message = raise (InvalidClientGroups message)

let read (value: JToken) : ClientGroup list =
    if isNull value then []
    elif value.Type <> JTokenType.Array then
        invalid "clientGroups must be an array of project and operationIds objects"
    else
        [ for group in value do
            if group.Type <> JTokenType.Object then
                invalid "Each clientGroups entry must be an object"
            let project = group.["project"]
            let operations = group.["operationIds"]
            if isNull project || project.Type <> JTokenType.String ||
               not (Regex.IsMatch(project.ToString(), @"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\z")) then
                invalid "A client group project must be a dotted identifier without path separators"
            if isNull operations || operations.Type <> JTokenType.Array || not operations.HasValues ||
               operations |> Seq.exists (fun item -> item.Type <> JTokenType.String || String.IsNullOrWhiteSpace(item.ToString())) then
                invalid "A client group needs a nonempty operationIds array of nonempty strings"
            yield { project = project.ToString(); operationIds = operations.ToObject<string list>() } ]

/// Validate the entire partition before the output directory is changed.
let validate (config: CodegenConfig) (operationIds: string list) =
    if not config.clientGroups.IsEmpty then
        if config.target <> Target.FSharp then
            invalid "clientGroups currently supports the fsharp target only"
        let projects = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        projects.Add config.project |> ignore
        let available = HashSet<string>(StringComparer.Ordinal)
        for operationId in operationIds do
            if String.IsNullOrWhiteSpace operationId || not (available.Add operationId) then
                invalid $"Grouped clients require unique nonempty source operation IDs: {operationId}"
        let assigned = HashSet<string>(StringComparer.Ordinal)
        for group in config.clientGroups do
            if not (projects.Add group.project) then
                invalid $"Duplicate client group project or shared model project: {group.project}"
            for operationId in group.operationIds do
                if not (available.Contains operationId) then
                    invalid $"Client group {group.project} names an unknown or filtered operation: {operationId}"
                if not (assigned.Add operationId) then
                    invalid $"Operation belongs to more than one client group: {operationId}"
        let missing = available |> Seq.filter (assigned.Contains >> not) |> Seq.sort |> Seq.toList
        if not missing.IsEmpty then
            invalid ("Client groups omit selected operations: " + String.concat ", " missing)
