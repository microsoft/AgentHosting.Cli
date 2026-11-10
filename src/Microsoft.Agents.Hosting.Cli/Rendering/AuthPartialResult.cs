using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Rendering;

internal sealed record AuthPartialResult(bool MetadataCommitted, bool CleanupIncomplete, AuthProfileInfo? Profile, string Guidance)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ProfileDeleted { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AuthProfileInfo? ActiveProfile { get; init; }
}
