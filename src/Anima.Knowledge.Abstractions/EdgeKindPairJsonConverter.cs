using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anima.Knowledge;

/// <summary>
/// Reads an <see cref="EdgeKindPair"/> from a two-element JSON array. Vocabulary
/// packs use the array form because it keeps the pair list visually compact.
/// </summary>
public sealed class EdgeKindPairJsonConverter : JsonConverter<EdgeKindPair>
{
    public override EdgeKindPair Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("edge pair must be a two-element array, for example [\"invoice\", \"repo\"]");
        }

        reader.Read();
        string? source = reader.GetString();
        reader.Read();
        string? destination = reader.GetString();
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("edge pair must contain exactly two kind names");
        }

        return new EdgeKindPair(
            source ?? throw new JsonException("edge pair source must be a string"),
            destination ?? throw new JsonException("edge pair destination must be a string"));
    }

    public override void Write(Utf8JsonWriter writer, EdgeKindPair value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartArray();
        writer.WriteStringValue(value.Source);
        writer.WriteStringValue(value.Destination);
        writer.WriteEndArray();
    }
}
