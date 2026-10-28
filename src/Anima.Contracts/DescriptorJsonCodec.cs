using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf.Reflection;

namespace Anima.Contracts;

/// <summary>
/// Descriptor-driven JSON codec. Every field is checked against the loaded
/// message descriptor, unknown fields are rejected, and canonical protobuf JSON
/// conventions are applied (camelCase names, int64 as string, enums by name,
/// bytes as base64, well-known types in their JSON forms).
/// </summary>
public sealed class DescriptorJsonCodec : IMessageCodec
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly HashSet<string> WrapperTypes =
    [
        "google.protobuf.DoubleValue",
        "google.protobuf.FloatValue",
        "google.protobuf.Int64Value",
        "google.protobuf.UInt64Value",
        "google.protobuf.Int32Value",
        "google.protobuf.UInt32Value",
        "google.protobuf.BoolValue",
        "google.protobuf.StringValue",
        "google.protobuf.BytesValue",
    ];

    private readonly ISchemaCatalog _catalog;
    private readonly ConcurrentDictionary<string, Dictionary<string, FieldDescriptor>> _fieldMaps =
        new(StringComparer.Ordinal);

    public DescriptorJsonCodec(ISchemaCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    public bool IsKnown(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return _catalog.Find(typeName) is not null;
    }

    public MessageDescriptor RequireDescriptor(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return _catalog.Find(typeName)
            ?? throw new KeyNotFoundException(
                $"Contract message '{typeName}' is not loaded. Recompile contracts with 'make contracts'.");
    }

    public JsonObject Parse(string typeName, string json)
    {
        var descriptor = RequireDescriptor(typeName);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, nodeOptions: null, DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new ContractValidationException($"{typeName}: invalid JSON: {exception.Message}");
        }

        if (node is not JsonObject payload)
        {
            throw new ContractValidationException($"{typeName}: payload must be a JSON object");
        }

        return NormalizeMessage(descriptor, payload, descriptor.FullName);
    }

    public string ToJson(JsonObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToJsonString();
    }

    public byte[] Encode(JsonObject value) => Encoding.UTF8.GetBytes(ToJson(value));

    public JsonObject Decode(string typeName, ReadOnlyMemory<byte> payload)
        => Parse(typeName, Encoding.UTF8.GetString(payload.Span));

    private JsonObject NormalizeMessage(MessageDescriptor descriptor, JsonObject payload, string path)
    {
        if (descriptor.FullName == "google.protobuf.Struct")
        {
            // Struct is arbitrary JSON; there is nothing to validate field-by-field.
            return (JsonObject)payload.DeepClone();
        }

        var fields = GetFieldMap(descriptor);
        var result = new JsonObject();

        foreach (var pair in payload)
        {
            if (!TryResolveField(fields, pair.Key, out var field))
            {
                throw new ContractValidationException(
                    $"{path}: unknown field '{pair.Key}' (known fields: {string.Join(", ", fields.Keys)})");
            }

            var normalized = NormalizeField(field, pair.Value, $"{path}.{field.JsonName}");
            if (normalized is not null)
            {
                result[field.JsonName] = normalized;
            }
        }

        return result;
    }

    private JsonNode? NormalizeField(FieldDescriptor field, JsonNode? value, string path)
    {
        if (field.IsMap)
        {
            return NormalizeMap(field, value, path);
        }

        if (field.IsRepeated)
        {
            if (value is not JsonArray array)
            {
                throw new ContractValidationException($"{path}: expected an array");
            }

            var items = new JsonArray();
            for (int index = 0; index < array.Count; index++)
            {
                items.Add(NormalizeSingle(field, array[index], $"{path}[{index}]"));
            }

            return items;
        }

        if (value is null)
        {
            if (field.FieldType is FieldType.Message or FieldType.Group)
            {
                return null;
            }

            if (field.FieldType == FieldType.Enum &&
                field.EnumType.FullName == "google.protobuf.NullValue")
            {
                return null;
            }

            throw new ContractValidationException($"{path}: null is not valid for {field.FieldType}");
        }

        return NormalizeSingle(field, value, path);
    }

    private JsonObject NormalizeMap(FieldDescriptor field, JsonNode? value, string path)
    {
        if (value is not JsonObject map)
        {
            throw new ContractValidationException($"{path}: expected an object for a map field");
        }

        var keyField = field.MessageType.FindFieldByNumber(1)
            ?? throw new ContractValidationException($"{path}: malformed map descriptor");
        var valueField = field.MessageType.FindFieldByNumber(2)
            ?? throw new ContractValidationException($"{path}: malformed map descriptor");

        var result = new JsonObject();
        foreach (var pair in map)
        {
            if (pair.Value is null)
            {
                throw new ContractValidationException($"{path}[{pair.Key}]: map values cannot be null");
            }

            string key = NormalizeMapKey(keyField, pair.Key, path);
            result[key] = NormalizeField(valueField, pair.Value, $"{path}[{pair.Key}]");
        }

        return result;
    }

    private static string NormalizeMapKey(FieldDescriptor keyField, string key, string path)
    {
        if (keyField.FieldType is FieldType.String or FieldType.Bool)
        {
            return key;
        }

        try
        {
            return keyField.FieldType switch
            {
                FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 =>
                    int.Parse(key, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                FieldType.UInt32 or FieldType.Fixed32 =>
                    uint.Parse(key, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 =>
                    long.Parse(key, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                FieldType.UInt64 or FieldType.Fixed64 =>
                    ulong.Parse(key, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                _ => key,
            };
        }
        catch (FormatException)
        {
            throw new ContractValidationException($"{path}: map key '{key}' is not a valid {keyField.FieldType}");
        }
        catch (OverflowException)
        {
            throw new ContractValidationException($"{path}: map key '{key}' is out of range");
        }
    }

    private JsonNode? NormalizeSingle(FieldDescriptor field, JsonNode? value, string path)
    {
        if (value is null)
        {
            if (field.FieldType is FieldType.Message or FieldType.Group)
            {
                return null;
            }

            throw new ContractValidationException($"{path}: null is not valid for {field.FieldType}");
        }

        if (field.FieldType is FieldType.Message or FieldType.Group)
        {
            return NormalizeMessageValue(field.MessageType, value, path);
        }

        var kind = value.GetValueKind();
        switch (field.FieldType)
        {
            case FieldType.String:
                return JsonValue.Create(RequireString(value, kind, path));

            case FieldType.Bool:
                if (kind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new ContractValidationException($"{path}: expected a boolean");
                }

                return JsonValue.Create(kind == JsonValueKind.True);

            case FieldType.Bytes:
                string bytes = RequireString(value, kind, path);
                try
                {
                    _ = Convert.FromBase64String(bytes);
                }
                catch (FormatException)
                {
                    throw new ContractValidationException($"{path}: expected base64 bytes");
                }

                return JsonValue.Create(bytes);

            case FieldType.Int32:
            case FieldType.SInt32:
            case FieldType.SFixed32:
                return JsonValue.Create(ParseInt32(value, kind, path));

            case FieldType.UInt32:
            case FieldType.Fixed32:
                return JsonValue.Create(ParseUInt32(value, kind, path));

            case FieldType.Int64:
            case FieldType.SInt64:
            case FieldType.SFixed64:
                return JsonValue.Create(ParseInt64(value, kind, path).ToString(CultureInfo.InvariantCulture));

            case FieldType.UInt64:
            case FieldType.Fixed64:
                return JsonValue.Create(ParseUInt64(value, kind, path).ToString(CultureInfo.InvariantCulture));

            case FieldType.Float:
            case FieldType.Double:
                return NormalizeFloatingPoint(value, kind, path);

            case FieldType.Enum:
                return NormalizeEnum(field, value, kind, path);

            default:
                throw new ContractValidationException($"{path}: unsupported field type {field.FieldType}");
        }
    }

    private JsonNode? NormalizeMessageValue(MessageDescriptor descriptor, JsonNode value, string path)
    {
        switch (descriptor.FullName)
        {
            case "google.protobuf.Timestamp":
                string timestamp = RequireString(value, value.GetValueKind(), path);
                if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                {
                    throw new ContractValidationException($"{path}: expected an RFC 3339 timestamp");
                }

                return JsonValue.Create(timestamp);

            case "google.protobuf.Duration":
                string duration = RequireString(value, value.GetValueKind(), path);
                if (!duration.EndsWith('s') || !double.TryParse(
                        duration[..^1],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out _))
                {
                    throw new ContractValidationException($"{path}: expected a duration like '3.5s'");
                }

                return JsonValue.Create(duration);

            case "google.protobuf.Struct":
            case "google.protobuf.Any":
                if (value is not JsonObject structValue)
                {
                    throw new ContractValidationException($"{path}: expected an object");
                }

                return structValue.DeepClone();

            case "google.protobuf.Value":
                return value.DeepClone();

            case "google.protobuf.ListValue":
                if (value is not JsonArray listValue)
                {
                    throw new ContractValidationException($"{path}: expected an array");
                }

                return listValue.DeepClone();
        }

        if (WrapperTypes.Contains(descriptor.FullName))
        {
            var inner = descriptor.Fields.InDeclarationOrder()[0];
            return NormalizeSingle(inner, value, path);
        }

        if (value is not JsonObject message)
        {
            throw new ContractValidationException($"{path}: expected an object for {descriptor.FullName}");
        }

        return NormalizeMessage(descriptor, message, path);
    }

    private static JsonValue NormalizeEnum(
        FieldDescriptor field,
        JsonNode value,
        JsonValueKind kind,
        string path)
    {
        if (kind == JsonValueKind.String)
        {
            string name = value.GetValue<string>();
            var enumValue = field.EnumType.FindValueByName(name)
                ?? throw new ContractValidationException(
                    $"{path}: unknown value '{name}' for enum {field.EnumType.FullName}");
            return JsonValue.Create(enumValue.Name);
        }

        if (kind == JsonValueKind.Number && value.AsValue().TryGetValue(out int number))
        {
            var enumValue = field.EnumType.FindValueByNumber(number);
            return enumValue is null
                ? JsonValue.Create(number)
                : JsonValue.Create(enumValue.Name);
        }

        throw new ContractValidationException($"{path}: expected an enum name or number");
    }

    private static JsonValue NormalizeFloatingPoint(JsonNode value, JsonValueKind kind, string path)
    {
        if (kind == JsonValueKind.Number)
        {
            return JsonValue.Create(value.GetValue<double>());
        }

        if (kind == JsonValueKind.String)
        {
            string text = value.GetValue<string>();
            if (text is "NaN" or "Infinity" or "-Infinity")
            {
                return JsonValue.Create(text);
            }
        }

        throw new ContractValidationException($"{path}: expected a number");
    }

    private static int ParseInt32(JsonNode value, JsonValueKind kind, string path)
    {
        if (TryGetIntegralText(value, kind, out string? text) &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        throw new ContractValidationException($"{path}: expected a 32-bit integer");
    }

    private static uint ParseUInt32(JsonNode value, JsonValueKind kind, string path)
    {
        if (TryGetIntegralText(value, kind, out string? text) &&
            uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint parsed))
        {
            return parsed;
        }

        throw new ContractValidationException($"{path}: expected an unsigned 32-bit integer");
    }

    private static long ParseInt64(JsonNode value, JsonValueKind kind, string path)
    {
        if (TryGetIntegralText(value, kind, out string? text) &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }

        throw new ContractValidationException($"{path}: expected a 64-bit integer");
    }

    private static ulong ParseUInt64(JsonNode value, JsonValueKind kind, string path)
    {
        if (TryGetIntegralText(value, kind, out string? text) &&
            ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed))
        {
            return parsed;
        }

        throw new ContractValidationException($"{path}: expected an unsigned 64-bit integer");
    }

    private static bool TryGetIntegralText(JsonNode value, JsonValueKind kind, out string text)
    {
        switch (kind)
        {
            case JsonValueKind.Number:
            case JsonValueKind.String:
                text = value.ToString();
                return true;
            default:
                text = string.Empty;
                return false;
        }
    }

    private static string RequireString(JsonNode value, JsonValueKind kind, string path)
    {
        if (kind != JsonValueKind.String)
        {
            throw new ContractValidationException($"{path}: expected a string");
        }

        return value.GetValue<string>();
    }

    private Dictionary<string, FieldDescriptor> GetFieldMap(MessageDescriptor descriptor)
        => _fieldMaps.GetOrAdd(descriptor.FullName, _ =>
        {
            var map = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal);
            foreach (var field in descriptor.Fields.InDeclarationOrder())
            {
                map[field.JsonName] = field;
                map.TryAdd(field.Name, field);
            }

            return map;
        });

    private static bool TryResolveField(
        Dictionary<string, FieldDescriptor> fields,
        string name,
        out FieldDescriptor field)
    {
        if (fields.TryGetValue(name, out field!))
        {
            return true;
        }

        var match = fields.FirstOrDefault(pair =>
            string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
        if (match.Key is not null)
        {
            field = match.Value;
            return true;
        }

        field = null!;
        return false;
    }
}
