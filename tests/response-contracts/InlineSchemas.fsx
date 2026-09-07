open Newtonsoft.Json.Linq

let source = JObject.Parse """{
  "openapi":"3.0.3",
  "components":{"schemas":{
    "Box":{"type":"object","properties":{
      "values":{"type":"array","items":{"oneOf":[{"type":"string"},{"type":"integer"}]}},
      "json":{"oneOf":[{"type":"object"},{"type":"array"}]},
      "text":{"type":"string","oneOf":[{"enum":["a"]},{"enum":["b"]}]}
    }},
    "Preserved":{"oneOf":[{"type":"object","properties":{"items":{"type":"array","items":{"oneOf":[{"type":"integer"},{"type":"string"}]}}}},{"type":"string"}]}
  }},
  "paths":{"/x":{"get":{"responses":{"200":{"content":{"application/json":{
    "schema":{"oneOf":[{"type":"string"},{"type":"integer"}]},
    "example":{"schema":{"oneOf":[{"type":"integer"},{"type":"string"}]}}
  }}}}}}}
}"""
let original = source.DeepClone()
let normalized, sites = InlineSchemas.promote source (Set.singleton "Preserved")
if not (JToken.DeepEquals(source, original)) then failwith "Promotion changed the source document"
let promoted = sites |> List.filter (fun site -> site.Name.IsSome)
if promoted.Length <> 2 then failwithf "Expected only array-item and response promotion: %A" sites
let item = promoted |> List.find (fun site -> site.SourcePointer.EndsWith("/values/items"))
let valueRef = normalized.SelectToken("components.schemas.Box.properties.values.items['$ref']").ToString()
if valueRef <> "#/components/schemas/" + item.Name.Value then failwith "Inline reference lost its declaration owner"
let alternate = source.DeepClone() :?> JObject
alternate.["info"] <- JObject(JProperty("title", "unrelated"))
let _, stable = InlineSchemas.promote alternate (Set.singleton "Preserved")
if stable <> sites then failwith "Unrelated document content changed source-pointer identities"
let examplePath = "paths['/x'].get.responses['200'].content['application/json'].example"
if not (JToken.DeepEquals(source.SelectToken(examplePath), normalized.SelectToken(examplePath))) then failwith "An example payload was rewritten as a schema"
if (sites |> List.filter (fun site -> site.Handling = "existing-json-value") |> List.length) <> 1 then failwith "Existing JSON field representation was not recorded"
let collision = source.DeepClone() :?> JObject
(collision.["components"].["schemas"] :?> JObject).Add(item.Name.Value, JObject(JProperty("type", "integer")))
try
    InlineSchemas.promote collision (Set.singleton "Preserved") |> ignore
    failwith "A promoted name overwrote a source definition"
with InlineSchemas.InvalidPromotion _ -> ()
printfn "Passed inline schema identity, source preservation, and collision checks."

let intersection = JObject.Parse """{
  "components":{"schemas":{"Choice":{"oneOf":[{"type":"string"},{"type":"object"}]}}},
  "paths":{"/compose":{"post":{"requestBody":{"content":{"application/json":{"schema":{
    "allOf":[{"$ref":"#/components/schemas/Choice"},{"type":"object","properties":{"extra":{"type":"integer"}}}]
  }}}}}}}
}"""
let promotedIntersection, intersectionSites = InlineSchemas.promote intersection Set.empty
match intersectionSites with
| [site] when site.Name.IsSome && site.SourcePointer.EndsWith("/requestBody/content/application~1json/schema") ->
    let definition = promotedIntersection.["components"].["schemas"].[site.Name.Value]
    if (definition.["allOf"] :?> JArray).Count <> 2 then failwith "Intersection lost an operand during promotion"
| sites -> failwithf "Composed union did not receive one canonical owner: %A" sites
printfn "Passed composed-union source-pointer and operand preservation check."

let aliasSource = JObject.Parse """{
  "components":{"schemas":{
    "Choice":{"oneOf":[{"type":"string"},{"type":"integer"}]},
    "Box":{"type":"object","properties":{
      "alias":{"allOf":[{"$ref":"#/components/schemas/Choice"},{"description":"annotation"}]},
      "alreadyRaw":{"allOf":[{"$ref":"#/components/schemas/Choice"},{"type":"string","maxLength":3}]}
    }}
  }}
}"""
let _, aliasSites = InlineSchemas.promote aliasSource Set.empty
if aliasSites |> List.exists (fun site -> site.Name.IsSome) then failwith "An existing alias or JSON-value field was unnecessarily promoted"
if aliasSites |> List.filter (fun site -> site.Handling = "existing-json-value") |> List.length <> 1 then failwith "Existing raw composition field was not inventoried"
printfn "Passed alias and existing JSON composition field handling checks."
