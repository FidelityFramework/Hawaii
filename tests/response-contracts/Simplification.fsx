open Newtonsoft.Json.Linq

let check input expected =
    let actual = Program.simplifyRedundantSchemaParts (JObject.Parse input)
    if not (JToken.DeepEquals(actual, JObject.Parse expected)) then
        failwithf "Unexpected simplification: %O" actual

check """{"type":"string","anyOf":[{"type":"string","maxLength":5}]}"""
      """{"type":"string","maxLength":5}"""
check """{"type":"string","oneOf":[{"type":"string","maxLength":5}]}"""
      """{"type":"string","maxLength":5}"""
check """{"type":"string","anyOf":[{"type":"integer"}]}"""
      """{"type":"string","anyOf":[{"type":"integer"}]}"""
check """{"maxLength":5,"oneOf":[{"type":"string","maxLength":10}]}"""
      """{"maxLength":5,"oneOf":[{"type":"string","maxLength":10}]}"""
check """{"type":"string","allOf":[{"type":"string","maxLength":5},{"example":"hi"}]}"""
      """{"type":"string","maxLength":5}"""
check """{"maxLength":5,"allOf":[{"maxLength":10},{"example":"hi"}]}"""
      """{"maxLength":5,"allOf":[{"maxLength":10},{"example":"hi"}]}"""
check """{"description":"outer","oneOf":[{"description":"inner","type":"object","properties":{"x":{"type":"string"}}}]}"""
      """{"description":"outer","type":"object","properties":{"x":{"type":"string"}}}"""
check """{"content":{"application/vnd.example+json":{"schema":{"type":"string"}}}}"""
      """{"content":{"application/vnd.example+json":{"schema":{"type":"string"}}}}"""
printfn "Passed 8 schema simplification checks."

let names = [ "a-b"; "a_b"; "$metadata"; "application/json"; "*"; "123" ]
let emitted = names |> List.map Helpers.sanitizeTypeName
if (Set.ofList emitted).Count <> names.Length then failwith "Schema name identities collapsed"
for name in emitted do
    if Helpers.sanitizeTypeName name <> name then failwith "Type-name normalization is not idempotent"
printfn "Passed schema name separation and idempotence checks."
