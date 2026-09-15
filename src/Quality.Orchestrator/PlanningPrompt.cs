using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace Quality.Orchestrator;

// Immutable, loaded once at startup. Paths are fixed; a job cannot choose files or fetch remote schemas.
public sealed class PlanningPrompt
{
    public const string Version = "plan/v2";
    public string SystemText { get; }
    public string PromptHash { get; }
    public string SchemaHash { get; }
    public JsonElement OutputSchema { get; }
    private readonly JsonSchema validator;

    public PlanningPrompt(string assetDirectory)
    {
        var directory = Path.Combine(assetDirectory, "prompts", "plan", "v2");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var root = manifest.RootElement;
        if (root.GetProperty("version").GetString() != Version || root.GetProperty("systemPrompt").GetString() != "system.md"
            || root.GetProperty("outputSchema").GetString() != "schemas/v1/planning-output.schema.json")
            throw new ArgumentException("Planning prompt manifest is invalid");
        var prompt = File.ReadAllBytes(Path.Combine(directory, "system.md"));
        var schema = File.ReadAllBytes(Path.Combine(assetDirectory, "schemas", "v1", "planning-output.schema.json"));
        PromptHash = Hash(prompt);
        SchemaHash = Hash(schema);
        if (PromptHash != root.GetProperty("promptSha256").GetString() || SchemaHash != root.GetProperty("schemaSha256").GetString())
            throw new ArgumentException("Planning prompt or schema checksum mismatch");
        SystemText = Encoding.UTF8.GetString(prompt);
        using var document = JsonDocument.Parse(schema);
        OutputSchema = document.RootElement.Clone();
        validator = JsonSchema.Build(OutputSchema);
    }

    public bool IsValid(JsonElement value) => validator.Evaluate(value).IsValid;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
