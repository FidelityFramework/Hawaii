module PreservedJson

/// A distinct schema-owned JSON value. Its converter preserves the payload,
/// without claiming that a union alternative has been selected or validated.
let declaration (name: string) =
    $"""

[<System.Text.Json.Serialization.JsonConverter(typeof<{name}_JsonConverter>)>]
type {name} private (jsonValue: System.Text.Json.JsonElement) =
    member _.JsonValue = jsonValue
    member _.Format() =
        match jsonValue.ValueKind with
        | System.Text.Json.JsonValueKind.String -> jsonValue.GetString()
        | System.Text.Json.JsonValueKind.Number
        | System.Text.Json.JsonValueKind.True
        | System.Text.Json.JsonValueKind.False -> jsonValue.GetRawText()
        | _ -> invalidOp "A scalar JSON value is required for this URL parameter"
    static member FromJson(value: System.Text.Json.JsonElement) =
        if value.ValueKind = System.Text.Json.JsonValueKind.Undefined then
            invalidArg "value" "A JSON value is required"
        {name}(value.Clone())

and private {name}_JsonConverter() =
    inherit System.Text.Json.Serialization.JsonConverter<{name}>()
    override _.HandleNull = true
    override _.Read(reader: byref<System.Text.Json.Utf8JsonReader>, _: System.Type, _: System.Text.Json.JsonSerializerOptions) =
        use document = System.Text.Json.JsonDocument.ParseValue(&reader)
        {name}.FromJson(document.RootElement)
    override _.Write(writer: System.Text.Json.Utf8JsonWriter, value: {name}, _: System.Text.Json.JsonSerializerOptions) =
        if obj.ReferenceEquals(value, null) then writer.WriteNullValue()
        else value.JsonValue.WriteTo(writer)
"""
