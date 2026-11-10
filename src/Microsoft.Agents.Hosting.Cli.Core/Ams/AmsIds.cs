using System.Diagnostics.CodeAnalysis;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

internal static class AmsIds
{
    [return: NotNullIfNotNull(nameof(id))]
    public static string? Local(string? id) => Guid.TryParse(id, out var guid) ? guid.ToString("D") : id;
}
