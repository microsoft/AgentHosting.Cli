namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

internal sealed class NoProgressStatus : IProgressStatus
{
    public static readonly NoProgressStatus Instance = new();

    public bool Report(string status) => false;
}
