using System.Text.Json;
using System.Text.Json.Serialization;

namespace Countflow.Modules.Rules.Persistence;

/// <summary>
/// One serialiser configuration, shared by the writer and the reader of a trace.
/// <para>
/// Two configurations would be two chances to disagree about casing, and a trace
/// that cannot be read back is not a trace. Enums travel as names for the same
/// reason they do on the wire: a stored <c>2</c> stops meaning anything the day
/// somebody inserts a member above it.
/// </para>
/// </summary>
internal static class TraceJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static string Serialise<T>(T value) => JsonSerializer.Serialize(value, Options);

    internal static T? Deserialise<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}
