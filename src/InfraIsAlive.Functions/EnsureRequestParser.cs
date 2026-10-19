using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace InfraIsAlive.Functions;

public static class EnsureRequestParser
{
    private const string Expected = "Expected a JSON body like { \"names\": [\"resource-name\"] } with at least one non-blank name.";

    public static bool TryParse(string body, out IReadOnlyList<string> names, [NotNullWhen(false)] out string? error)
    {
        names = [];
        error = Expected;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("names", out var array)
                || array.ValueKind != JsonValueKind.Array
                || array.GetArrayLength() == 0)
                return false;

            var parsed = new List<string>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) return false;
                parsed.Add(item.GetString()!);
            }

            names = parsed;
            error = null;
            return true;
        }
    }
}
