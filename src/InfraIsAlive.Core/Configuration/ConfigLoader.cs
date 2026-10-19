using System.Text.Json;
using System.Text.RegularExpressions;

namespace InfraIsAlive.Core.Configuration;

/// <summary>
/// Loads the built-in profiles and the adopter's configuration, merges them (an adopter profile with the
/// same key replaces the built-in one entirely) and validates the result, reporting every error at once.
/// </summary>
public static partial class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [GeneratedRegex(@"^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/.+/.+$", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceIdPattern();

    public static InfraConfig Load(string builtinProfilesPath, string adopterConfigPath)
    {
        var builtin = Read(builtinProfilesPath);
        var adopter = Read(adopterConfigPath);

        var errors = new List<string>();
        var types = new Dictionary<string, ResourceTypeProfile>(StringComparer.OrdinalIgnoreCase);

        var mergedTypes = new Dictionary<string, ProfileDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, dto) in builtin.ResourceTypes ?? []) mergedTypes[key] = dto;
        foreach (var (key, dto) in adopter.ResourceTypes ?? []) mergedTypes[key] = dto;

        foreach (var (key, dto) in mergedTypes)
        {
            var profile = BuildProfile(key, dto, errors);
            if (profile is not null) types[key] = profile;
        }

        var resources = new Dictionary<string, RegisteredResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, dto) in adopter.Resources ?? [])
        {
            var resourceErrors = errors.Count;
            if (string.IsNullOrWhiteSpace(dto.Type) || !mergedTypes.ContainsKey(dto.Type))
                errors.Add($"Resource '{name}': unknown Type '{dto.Type}'.");
            if (string.IsNullOrWhiteSpace(dto.ResourceId) || !ResourceIdPattern().IsMatch(dto.ResourceId))
                errors.Add($"Resource '{name}': ResourceId '{dto.ResourceId}' is not a full ARM resource ID.");
            if (errors.Count == resourceErrors) resources[name] = new RegisteredResource(dto.Type!, dto.ResourceId!.TrimEnd('/'));
        }

        if (errors.Count > 0) throw new InfraConfigException(errors);

        return new InfraConfig(adopter.AllowedCallerObjectIds ?? [], types, resources);
    }

    private static ResourceTypeProfile? BuildProfile(string key, ProfileDto dto, List<string> errors)
    {
        var before = errors.Count;
        if (string.IsNullOrWhiteSpace(dto.ApiVersion)) errors.Add($"ResourceType '{key}': ApiVersion is required.");
        if (string.IsNullOrWhiteSpace(dto.StatusField)) errors.Add($"ResourceType '{key}': StatusField is required.");
        if (dto.StateMap is null || dto.StateMap.Count == 0) errors.Add($"ResourceType '{key}': StateMap must not be empty.");

        var stateMap = new Dictionary<string, MappedState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, target) in dto.StateMap ?? [])
        {
            if (Enum.TryParse<MappedState>(target, ignoreCase: true, out var mapped) && Enum.IsDefined(mapped))
                stateMap[raw] = mapped;
            else
                errors.Add($"ResourceType '{key}': StateMap value '{target}' for '{raw}' must be one of {string.Join(", ", Enum.GetNames<MappedState>())}.");
        }

        if (errors.Count > before) return null;

        return new ResourceTypeProfile(
            dto.ApiVersion!,
            dto.StatusPath ?? "",
            dto.StatusField!,
            string.IsNullOrWhiteSpace(dto.StartAction) ? null : dto.StartAction,
            stateMap,
            dto.RequiredActions ?? []);
    }

    private static FileDto Read(string path)
    {
        if (!File.Exists(path)) throw new InfraConfigException([$"Configuration file not found: {path}"]);
        try
        {
            return JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), JsonOptions) ?? new FileDto();
        }
        catch (JsonException ex)
        {
            throw new InfraConfigException([$"Configuration file {path} is not valid JSON: {ex.Message}"]);
        }
    }

    private sealed class FileDto
    {
        public List<string>? AllowedCallerObjectIds { get; set; }
        public Dictionary<string, ProfileDto>? ResourceTypes { get; set; }
        public Dictionary<string, ResourceDto>? Resources { get; set; }
    }

    private sealed class ProfileDto
    {
        public string? ApiVersion { get; set; }
        public string? StatusPath { get; set; }
        public string? StatusField { get; set; }
        public string? StartAction { get; set; }
        public Dictionary<string, string>? StateMap { get; set; }
        public List<string>? RequiredActions { get; set; }
    }

    private sealed class ResourceDto
    {
        public string? Type { get; set; }
        public string? ResourceId { get; set; }
    }
}
