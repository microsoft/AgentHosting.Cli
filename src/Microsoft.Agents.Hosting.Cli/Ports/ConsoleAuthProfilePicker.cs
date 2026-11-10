using System.Globalization;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Ports;

/// <summary>A metadata-only profile picker with no implicit first-profile selection.</summary>
public sealed class ConsoleAuthProfilePicker(TextReader input, TextWriter output) : IAuthProfilePicker
{
    public async Task<string?> PickAsync(IReadOnlyList<AuthProfileInfo> profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        cancellationToken.ThrowIfCancellationRequested();
        for (int index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            await output.WriteLineAsync(TerminalText.Sanitize(
                $"{index + 1} {(profile.IsActive ? "*" : " ")} {profile.Name ?? "(unnamed)"} | {profile.Account} | {profile.Cloud}")).ConfigureAwait(false);
        }
        if (profiles.Count == 0) return null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteAsync("Select a profile number (q to cancel): ").ConfigureAwait(false);
            var answer = (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
            if (answer is null || answer.Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
            if (int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
                && position > 0 && position <= profiles.Count)
                return profiles[position - 1].ProfileId;
            await output.WriteLineAsync("Supply one of the listed profile numbers.").ConfigureAwait(false);
        }
    }
}
