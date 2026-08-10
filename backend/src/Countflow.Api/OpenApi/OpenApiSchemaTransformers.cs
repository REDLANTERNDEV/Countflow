using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Countflow.Api.OpenApi;

public static class OpenApiSchemaTransformers
{
    /// <summary>
    /// Publishes enums as their names rather than their ordinals.
    /// <para>
    /// Numeric enums in a public contract are a trap: inserting a member
    /// renumbers everything after it and silently breaks every client that
    /// stored the old value. <c>type=PickFace</c> also reads far better in a log
    /// or a support ticket than <c>type=4</c>.
    /// </para>
    /// </summary>
    public static Task EnumsAsStrings(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;

        if (!type.IsEnum)
        {
            return Task.CompletedTask;
        }

        schema.Type = JsonSchemaType.String;
        schema.Format = null;
        schema.Pattern = null;
        schema.Enum = [.. Enum.GetNames(type).Select(name => (JsonNode)name)];

        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes the string alternative .NET adds to every numeric schema.
    /// <para>
    /// .NET 10 describes an <c>int</c> as <c>["integer", "string"]</c> so that
    /// string-encoded numbers validate. Faithful, but it means every quantity in
    /// a generated client lands as <c>string | number</c> and each one needs a
    /// cast before arithmetic. The API only ever emits real numbers, so the
    /// published contract should say so.
    /// </para>
    /// </summary>
    public static Task NumbersAreNotStrings(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (schema.Type is not { } type || !type.HasFlag(JsonSchemaType.String))
        {
            return Task.CompletedTask;
        }

        var isNumeric = type.HasFlag(JsonSchemaType.Integer) || type.HasFlag(JsonSchemaType.Number);
        if (!isNumeric)
        {
            return Task.CompletedTask;
        }

        schema.Type = type & ~JsonSchemaType.String;

        // The pattern only existed to validate the string form.
        schema.Pattern = null;

        return Task.CompletedTask;
    }
}
