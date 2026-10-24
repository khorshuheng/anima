namespace Anima.Contracts;

/// <summary>Content types carried on bus envelopes.</summary>
public static class ContractContentTypes
{
    /// <summary>JSON projection of a protobuf message, validated against its descriptor.</summary>
    public const string ProtobufJson = "application/x-protobuf+json";

    /// <summary>Unvalidated JSON for envelopes without a loaded contract.</summary>
    public const string Json = "application/json";
}
