using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Countflow.Api.OpenApi;

public static class OpenApiExporter
{
    private const string DocumentName = "v1";

    /// <summary>
    /// Writes the OpenAPI document to disk and exits.
    /// <para>
    /// This exists so CI can regenerate the TypeScript client without standing
    /// up Postgres and the whole stack first: registering a DbContext does not
    /// open a connection, so the document can be produced from endpoint
    /// metadata alone.
    /// </para>
    /// </summary>
    public static async Task<int> ExportAsync(WebApplication app, string path)
    {
        // Endpoints only reach the DI endpoint data source once the host starts,
        // so a document generated before StartAsync comes back with no paths.
        // Port 0 lets the OS pick a free port — nothing ever connects to it, and
        // it keeps concurrent CI jobs from colliding.
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");

        await app.StartAsync();

        try
        {
            // AddOpenApi registers one provider per document, keyed by document name.
            var provider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(DocumentName);
            var document = await provider.GetOpenApiDocumentAsync();

            return await WriteAsync(document, path);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task<int> WriteAsync(Microsoft.OpenApi.OpenApiDocument document, string path)
    {

        await using var buffer = new MemoryStream();
        await document.SerializeAsJsonAsync(buffer, OpenApiSpecVersion.OpenApi3_1);
        buffer.Position = 0;

        // Re-serialise with sorted, indented output. The generator's key order is
        // not stable across runs, and unstable ordering would make CI's
        // drift check fail on noise instead of on real contract changes.
        var node = JsonNode.Parse(buffer);
        var normalised = JsonSerializer.Serialize(node, SerializerOptions);

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, normalised + Environment.NewLine);

        Console.WriteLine($"OpenAPI document written to {fullPath}");
        return 0;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };
}
