using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

internal static class SyncDataverseCall
{
    /// <summary>Runs the call, restating a failed Dataverse request as a service error of the named operation that never echoes the response body.</summary>
    /// <typeparam name="T">The call's result.</typeparam>
    /// <param name="operation">The operation named in the service error.</param>
    /// <param name="call">The sync library call.</param>
    /// <returns>The call's result.</returns>
    public static async Task<T> RunAsync<T>(string operation, Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (DataverseRequestException failure)
        {
            throw CopilotStudioException.ServiceError(operation, failure.Describe());
        }
    }
}
