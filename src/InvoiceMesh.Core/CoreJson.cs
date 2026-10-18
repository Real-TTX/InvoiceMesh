using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvoiceMesh.Core;

/// <summary>
/// How the core writes its JSON (schemas, mappings, extractions, reports): camelCase, enums as words, umlauts as they are. The database stores these
/// documents in <c>jsonb</c> columns, and an exported schema or mapping is the same text.
/// </summary>
public static class CoreJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);

    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string Serialize<T>(T value, bool indented = false) => JsonSerializer.Serialize(value, indented ? Indented : Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("The JSON is empty.");
}
