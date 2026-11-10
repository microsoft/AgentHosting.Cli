using System.Net;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>One attempt at an Agent Management Service request, where a request that recovers a lost answer is sent once more, with a new client request id, when the answer to its first attempt is lost.</summary>
public sealed class AmsRequestAttempt
{
    private readonly string? _lostClientRequestId;

    /// <summary>Creates the only attempt of a request that does not recover a lost answer.</summary>
    public AmsRequestAttempt()
    {
    }

    private AmsRequestAttempt(bool recoversLostAnswer, string? lostClientRequestId)
    {
        RecoversLostAnswer = recoversLostAnswer;
        _lostClientRequestId = lostClientRequestId;
    }

    /// <summary>The client request id the attempt sends.</summary>
    public string ClientRequestId { get; } = Guid.NewGuid().ToString("D");

    /// <summary>The client request ids a failure of the attempt names, which on a replay are the id of the request whose answer was lost and then the replay's.</summary>
    public string ReportedClientRequestId => _lostClientRequestId is null ? ClientRequestId : $"{_lostClientRequestId}, replayClientRequestId={ClientRequestId}";

    /// <summary>Whether the request is replayed once when the answer to its first attempt is lost.</summary>
    public bool RecoversLostAnswer { get; }

    /// <summary>Sends a request and, when it recovers a lost answer and the answer to its first attempt was lost, sends it once more as a replay.</summary>
    /// <typeparam name="T">What the request returns.</typeparam>
    /// <param name="recoversLostAnswer">Whether the request is replayed once when the answer to its first attempt is lost.</param>
    /// <param name="send">Sends one attempt of the request.</param>
    public static async Task<T> SendAsync<T>(bool recoversLostAnswer, Func<AmsRequestAttempt, Task<T>> send)
    {
        ArgumentNullException.ThrowIfNull(send);

        var first = new AmsRequestAttempt(recoversLostAnswer, null);
        try
        {
            return await send(first).ConfigureAwait(false);
        }
        catch (AnswerLostException)
        {
            return await send(new AmsRequestAttempt(recoversLostAnswer, first.ClientRequestId)).ConfigureAwait(false);
        }
    }

    /// <summary>Returns whether the attempt asks the service to answer at once, which a replay always does.</summary>
    /// <param name="requested">Whether the caller asked the service to answer at once.</param>
    public bool PrefersRespondAsync(bool requested) => requested || _lostClientRequestId is not null;

    /// <summary>Returns what the attempt throws for a failure that lost its answer: the signal that sends a request that recovers a lost answer once more after its first attempt, otherwise the failure.</summary>
    /// <param name="failure">The failure the attempt reports when the request is not replayed.</param>
    public Exception AnswerLost(Exception failure) => RecoversLostAnswer && _lostClientRequestId is null ? new AnswerLostException(failure) : failure;

    /// <summary>Returns what the attempt throws for a refusal, which loses the answer when the gateway timed out waiting for the service.</summary>
    /// <param name="status">The status of the refusal.</param>
    /// <param name="refusal">The refusal the attempt reports when the request is not replayed.</param>
    public Exception Refused(HttpStatusCode status, Exception refusal) => status == HttpStatusCode.GatewayTimeout ? AnswerLost(refusal) : refusal;

    private sealed class AnswerLostException(Exception failure) : Exception(failure.Message, failure);
}
