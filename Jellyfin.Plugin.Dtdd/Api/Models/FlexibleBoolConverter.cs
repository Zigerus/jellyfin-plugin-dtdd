using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Dtdd.Api.Models;

/// <summary>
/// DoesTheDogDie is inconsistent about boolean encoding: the same field
/// (e.g., <c>isSpoiler</c>) comes back as a JSON Number (0/1) in
/// <c>/dddsearch</c> responses but as a real JSON Boolean inside
/// <c>topicItemStats[].topic</c> in <c>/media/{id}</c> responses. System.Text.Json
/// by default refuses to coerce Number → Boolean, which surfaces as
/// "DTDD JSON parse error on /dddsearch?q=…" when the topic-seed runs.
///
/// <para>
/// This converter accepts True/False, 0/1 (Int), and the case-insensitive
/// strings "true"/"false". Writes always emit a real JSON Boolean.
/// </para>
/// </summary>
public class FlexibleBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var i))
                {
                    return i != 0;
                }
                return reader.GetDouble() != 0d;
            case JsonTokenType.String:
                var s = reader.GetString();
                return bool.TryParse(s, out var b) && b;
            case JsonTokenType.Null:
                return false;
            default:
                throw new JsonException($"Cannot convert {reader.TokenType} to bool");
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        writer.WriteBooleanValue(value);
    }
}
