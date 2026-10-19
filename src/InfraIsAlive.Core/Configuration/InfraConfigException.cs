namespace InfraIsAlive.Core.Configuration;

public sealed class InfraConfigException(IReadOnlyList<string> errors)
    : Exception("Invalid InfraIsAlive configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
