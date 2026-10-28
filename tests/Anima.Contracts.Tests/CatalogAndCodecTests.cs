using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Contracts.Tests;

public sealed class CatalogAndCodecTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "anima-contracts-tests-" + Guid.NewGuid().ToString("n"));

    public CatalogAndCodecTests()
    {
        Directory.CreateDirectory(_directory);
        var file = new FileDescriptorProto
        {
            Name = "anima/test/v1/test.proto",
            Package = "anima.test.v1",
            Syntax = "proto3",
        };

        var message = new DescriptorProto { Name = "Ping" };
        message.Field.Add(new FieldDescriptorProto
        {
            Name = "text",
            JsonName = "text",
            Number = 1,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.String,
        });
        message.Field.Add(new FieldDescriptorProto
        {
            Name = "count",
            JsonName = "count",
            Number = 2,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Int32,
        });
        file.MessageType.Add(message);

        var set = new FileDescriptorSet();
        set.File.Add(file);
        File.WriteAllBytes(Path.Combine(_directory, "test.pb"), set.ToByteArray());
    }

    [Fact]
    public void Catalog_loads_messages_from_descriptor_sets()
    {
        var catalog = CreateCatalog();

        Assert.True(catalog.TryFind("anima.test.v1.Ping", out var descriptor));
        Assert.Equal("Ping", descriptor.Name);
        Assert.Equal(2, descriptor.Fields.InDeclarationOrder().Count);
    }

    [Fact]
    public void Codec_validates_and_normalizes_descriptor_driven_json()
    {
        var codec = new DescriptorJsonCodec(CreateCatalog());

        var payload = codec.Parse("anima.test.v1.Ping", """{"text":"hello","count":"3"}""");
        string json = codec.ToJson(payload);

        Assert.Contains("\"hello\"", json, StringComparison.Ordinal);
        Assert.Contains("\"count\":3", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Codec_rejects_unknown_fields()
    {
        var codec = new DescriptorJsonCodec(CreateCatalog());

        var exception = Assert.Throws<ContractValidationException>(
            () => codec.Parse("anima.test.v1.Ping", """{"text":"hello","bogus":1}"""));

        Assert.Contains("unknown field 'bogus'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Codec_rejects_wrong_scalar_types()
    {
        var codec = new DescriptorJsonCodec(CreateCatalog());

        var exception = Assert.Throws<ContractValidationException>(
            () => codec.Parse("anima.test.v1.Ping", """{"count":"not-a-number"}"""));

        Assert.Contains("32-bit integer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Codec_round_trips_through_bus_bytes()
    {
        var codec = new DescriptorJsonCodec(CreateCatalog());

        var payload = codec.Parse("anima.test.v1.Ping", """{"text":"hello","count":3}""");
        byte[] bytes = codec.Encode(payload);
        var decoded = codec.Decode("anima.test.v1.Ping", bytes);

        Assert.Equal("hello", decoded["text"]!.GetValue<string>());
        Assert.Equal(3, decoded["count"]!.GetValue<int>());
    }

    [Fact]
    public void Codec_rejects_unknown_contract_with_actionable_message()
    {
        var codec = new DescriptorJsonCodec(CreateCatalog());

        var exception = Assert.Throws<KeyNotFoundException>(
            () => codec.Parse("anima.missing.v1.Nope", "{}"));

        Assert.Contains("make contracts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catalog_reload_picks_up_new_descriptor_files()
    {
        var catalog = CreateCatalog();
        Assert.False(catalog.TryFind("anima.test.v1.Pong", out _));

        var file = new FileDescriptorProto
        {
            Name = "anima/test/v1/pong.proto",
            Package = "anima.test.v1",
            Syntax = "proto3",
        };
        file.MessageType.Add(new DescriptorProto { Name = "Pong" });
        var set = new FileDescriptorSet();
        set.File.Add(file);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "pong.pb"), set.ToByteArray());

        await catalog.ReloadAsync();

        Assert.True(catalog.TryFind("anima.test.v1.Pong", out _));
    }

    private FileDescriptorSetCatalog CreateCatalog() => new(
        Options.Create(new SchemaCatalogOptions
        {
            DescriptorDirectory = _directory,
            WatchForChanges = false,
        }),
        NullLogger<FileDescriptorSetCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
