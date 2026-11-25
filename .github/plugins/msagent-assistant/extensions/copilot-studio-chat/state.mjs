export const Status = Object.freeze({
    Unchecked: "unchecked",
    SetupRequired: "setup-required",
    Ready: "ready",
    Sending: "sending",
    AuthenticationRequired: "authentication-required",
    Changed: "changed",
    Uncertain: "uncertain",
    Error: "error",
});

export function classifyFailure(error, { status, requiresReset, conversationIssue, turnAttempted, startAttempted }) {
    const failure = { status, requiresReset, conversationIssue, error: error.message };
    if (turnAttempted && (startAttempted || error.data?.queryDelivery !== "not-attempted")) {
        failure.requiresReset = true;
        failure.status = Status.Uncertain;
        failure.error += " Remote effects may have occurred. Check them before starting a new conversation.";
        failure.conversationIssue = startAttempted
            ? `The chat couldn't start. ${error.message} Start a new chat to try again.`
            : error.authRequired
                ? `${error.message} Start a new chat to continue.`
                : "We couldn't confirm whether the agent received the last message. Start a new chat to continue.";
    } else if (error.authRequired) {
        failure.status = Status.AuthenticationRequired;
    } else if (![Status.Changed, Status.AuthenticationRequired, Status.Uncertain].includes(status)) {
        failure.status = Status.Error;
    }
    return failure;
}
