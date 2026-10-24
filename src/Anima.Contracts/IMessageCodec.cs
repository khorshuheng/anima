using System.Text.Json.Nodes;
using Google.Protobuf.Reflection;

namespace Anima.Contracts;

/// <summary>
/// Validates and normalizes user-defined contract payloads against their runtime
/// descriptor. Protobuf remains the IDL; the wire form on the bus is the
/// descriptor-driven JSON projection, so no generated C# type is required.
/// </summary>
public interface IMessageCodec
{
    bool IsKnown(string typeName);

    MessageDescriptor RequireDescriptor(string typeName);

    /// <summary>Validate a JSON payload and return its canonical form.</summary>
    JsonObject Parse(string typeName, string json);

    string ToJson(JsonObject value);

    byte[] Encode(JsonObject value);

    JsonObject Decode(string typeName, ReadOnlyMemory<byte> payload);
}
