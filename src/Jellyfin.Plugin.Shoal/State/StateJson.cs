using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Shoal.State;

/// <summary>
/// The JSON rules for <c>state.json</c>: camelCase names, enum names as text (never numbers), ids in the 32-hex "N" form,
/// strict reading (no comments, no trailing commas, no duplicate names, no nulls where a value is required) and depth 32.
/// </summary>
internal static class StateJson
{
    /// <summary>The most bytes a state file may have; anything larger is treated as unreadable.</summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>
    /// Serializes a state (indented, so an admin can read it).
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>UTF-8 JSON.</returns>
    public static byte[] Serialize(ShoalState state) => JsonSerializer.SerializeToUtf8Bytes(state, Options);

    /// <summary>
    /// Parses a state. Throws on anything malformed.
    /// </summary>
    /// <param name="utf8">The file content.</param>
    /// <returns>The state, not yet validated.</returns>
    /// <exception cref="JsonException">The content is not a valid state document.</exception>
    public static ShoalState Deserialize(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<ShoalState>(utf8, Options) ?? throw new JsonException("State document is null.");

    /// <summary>
    /// Reads only the <c>schema</c> number, so a newer file can be recognized before its other members are interpreted.
    /// </summary>
    /// <param name="utf8">The file content.</param>
    /// <returns>The schema number, or null when the document is not an object with an integer <c>schema</c>.</returns>
    public static int? PeekSchema(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 32 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            var depth = 1;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        depth++;
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        depth--;
                        break;
                    case JsonTokenType.PropertyName when depth == 1 && reader.ValueTextEquals("schema"u8):
                        return reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var schema) ? schema : null;
                    default:
                        break;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = true,
            MaxDepth = 32,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            AllowDuplicateProperties = false,
            NumberHandling = JsonNumberHandling.Strict,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        options.Converters.Add(new GuidNJsonConverter());
        return options;
    }
}

/// <summary>
/// Writes ids in the 32-digit "N" form (as Jellyfin's own API does) and reads any form of a GUID. Also works for dictionary keys.
/// </summary>
internal sealed class GuidNJsonConverter : JsonConverter<Guid>
{
    /// <inheritdoc />
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Guid.TryParse(reader.GetString(), out var value) ? value : throw new JsonException("Invalid id.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("N"));

    /// <inheritdoc />
    public override Guid ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Read(ref reader, typeToConvert, options);

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.ToString("N"));
}
