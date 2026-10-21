using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>Creates a lazy output-owning operation; no sign-in or network work occurs in dispatch.</summary>
public sealed class LiveTestCommandHandler(
    LiveTestTargetResolver resolver,
    MsalLiveTestTokenProvider tokens,
    ITokenProvider tokenProvider) : ICommandHandler<LiveTestRequest, ILiveTestStreamingResult>
{
    public Task<ILiveTestStreamingResult> HandleAsync(LiveTestRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<ILiveTestStreamingResult>(
            new LiveTestExecution(request,
                resolver.Resolve,
                (target, nonInteractive, token) =>
                    tokens.AcquireDelegatedTokenAsync(target.TenantId, nonInteractive, token),
                // The preflight may consent interactively; SDK callbacks use the shared silent provider.
                (target, _, token) => new McsLiveTestSession(
                    new McsLiveTestTarget(
                        target.EnvironmentId,
                        target.TenantId,
                        target.SchemaName),
                    tokenProvider,
                    cancellationToken: token)));
}
