using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Quality.Domain;

namespace Quality.Orchestrator;

public sealed record RequirementSourceOptions(
    string? JiraBaseUrl = null, string? JiraEmail = null, string? JiraToken = null,
    string? JiraAcceptanceField = null, string? CodaToken = null,
    string? CodaTitleColumn = null, string? CodaDescriptionColumn = null,
    string? CodaAcceptanceColumn = null);

// Only explicit source fields become criteria; prose in a description is never guessed into requirements.
public sealed class RemoteRequirementSource(HttpClient http, RequirementSourceOptions options) : IRequirementSource
{
    public async Task<Requirement> NormalizeAsync(RequirementReference reference, CancellationToken ct)
    {
        reference.Validate();
        if (reference.Source == "stub") return await new StubRequirementSource().NormalizeAsync(reference, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return reference.Source == "jira"
            ? await JiraAsync(reference, timeout.Token)
            : await CodaAsync(reference, timeout.Token);
    }

    private async Task<Requirement> JiraAsync(RequirementReference reference, CancellationToken ct)
    {
        if (!Uri.TryCreate(options.JiraBaseUrl, UriKind.Absolute, out var origin) || origin.Scheme != "https"
            || origin.AbsolutePath != "/" || origin.Query != "" || origin.Fragment != "" || origin.UserInfo != "")
            throw new ArgumentException("Configure an HTTPS Jira origin");
        Require(options.JiraEmail); Require(options.JiraToken);
        var field = options.JiraAcceptanceField;
        if (field is not null && !Regex.IsMatch(field, "^customfield_[0-9]+$"))
            throw new ArgumentException("Jira acceptance field must be a customfield ID");
        var fields = "summary,description,updated" + (field is null ? "" : $",{field}");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(origin, $"rest/api/3/issue/{Uri.EscapeDataString(reference.Id)}?fields={fields}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.JiraEmail}:{options.JiraToken}")));
        using var document = await ReadAsync(request, ct);
        var root = document.RootElement;
        if (RequiredText(root, "key") != reference.Id) throw new InvalidDataException("Issue key mismatch");
        var resource = RequiredText(root, "id");
        var data = root.GetProperty("fields");
        var revision = RequiredText(data, "updated");
        var title = RequiredText(data, "summary");
        var description = data.TryGetProperty("description", out var body) ? RichText(body) : "";
        var criteria = field is not null && data.TryGetProperty(field, out var ac) ? Criteria(ac, "jira", resource, revision, field) : [];
        return Build(reference, title, description, revision, criteria);
    }

    private async Task<Requirement> CodaAsync(RequirementReference reference, CancellationToken ct)
    {
        Require(options.CodaToken); Require(options.CodaTitleColumn); Require(options.CodaDescriptionColumn);
        // A row is the atomic requirement; its document and table are explicit in the reference.
        var parts = reference.Id.Split('/');
        if (parts.Length != 3 || parts.Any(p => !Regex.IsMatch(p, "^[A-Za-z0-9_-]+$")))
            throw new ArgumentException("Coda reference must be docId/tableId/rowId");
        var resource = $"docs/{parts[0]}/tables/{parts[1]}/rows/{parts[2]}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://coda.io/apis/v1/{resource}?valueFormat=simple&useColumnNames=false");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.CodaToken);
        using var document = await ReadAsync(request, ct);
        var root = document.RootElement;
        if (RequiredText(root, "id") != parts[2]) throw new InvalidDataException("Row ID mismatch");
        var revision = RequiredText(root, "updatedAt");
        var values = root.GetProperty("values");
        var title = RequiredText(values, options.CodaTitleColumn!);
        var description = values.TryGetProperty(options.CodaDescriptionColumn!, out var body) ? RichText(body) : "";
        var field = options.CodaAcceptanceColumn;
        var criteria = field is not null && values.TryGetProperty(field, out var ac) ? Criteria(ac, "coda", resource, revision, field) : [];
        return Build(reference, title, description, revision, criteria);
    }

    private async Task<JsonDocument> ReadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var retryAfter = response.Headers.RetryAfter;
            throw new RequirementRequestException(response.StatusCode,
                retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow));
        }
        // Bound both compressed/unknown-length responses and parsing memory.
        using var buffer = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Source response too large");
            buffer.Write(bytes, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    private static Requirement Build(RequirementReference reference, string title, string description,
        string revision, AcceptanceCriterion[] criteria) => new($"{reference.Source}:{reference.Id}", reference,
        title, description, [], [], criteria,
        criteria.Length == 0 ? ["No explicit acceptance criteria imported; configure the source field or complete the source requirement"] : [],
        revision, false);

    private static AcceptanceCriterion[] Criteria(JsonElement value, string provider, string resource, string revision, string field)
    {
        // Keep a text field intact: splitting sentences or lines can change a criterion's meaning.
        var entries = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
        return entries.Select((entry, index) => (text: RichText(entry).Trim(), index))
            .Where(e => e.text.Length > 0)
            .Select(e => new AcceptanceCriterion("AC-" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{provider}:{resource}:{field}:{e.index}")))[..16], e.text,
                new(provider, resource, revision, field, value.ValueKind == JsonValueKind.Array ? $"/{field}/{e.index}" : $"/{field}")))
            .ToArray();
    }

    private static string RequiredText(JsonElement root, string key)
    {
        var value = root.GetProperty(key).GetString();
        Require(value);
        return value!;
    }
    private static void Require(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Required source configuration or field is missing");
    }
    private static string RichText(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Unsupported source text format");
        var type = value.GetProperty("type").GetString();
        if (type == "text") return value.GetProperty("text").GetString()!;
        if (type == "hardBreak") return "\n";
        if (!value.TryGetProperty("content", out var content)) throw new InvalidDataException("Unsupported rich text node");
        var text = string.Concat(content.EnumerateArray().Select(RichText));
        return type is "paragraph" or "heading" or "listItem" ? text + "\n" : text;
    }
}
