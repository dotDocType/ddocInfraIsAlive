using Newtonsoft.Json.Linq;

namespace InfraIsAlive.Core.Evaluation;

public static class StateExtractor
{
    /// <summary>
    /// Selects the raw state from an ARM response with a JSONPath expression (Newtonsoft syntax, filters
    /// supported). Returns the first match as a string, or null when nothing matches.
    /// </summary>
    public static string? Extract(string json, string statusField)
    {
        var token = JToken.Parse(json).SelectTokens(statusField).FirstOrDefault();
        return token is null || token.Type == JTokenType.Null ? null : token.ToString();
    }
}
