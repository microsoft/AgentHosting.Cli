using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Evaluations;

/// <summary>The limits the Copilot Studio evaluation service places on an imported test set.</summary>
public static class EvaluationConversationLimits
{
    /// <summary>The most question-and-answer pairs one conversation may hold.</summary>
    public const int MaximumExchangesPerConversation = 6;

    /// <summary>The most conversations one test set may hold.</summary>
    public const int MaximumConversations = 20;

    /// <summary>The most characters one question may carry, including spaces.</summary>
    public const int MaximumQuestionLength = 1000;

    /// <summary>Rejects a set of conversations the service would refuse.</summary>
    /// <param name="conversations">The conversations to check.</param>
    public static void Validate(IReadOnlyList<EvaluationConversation> conversations)
    {
        ArgumentNullException.ThrowIfNull(conversations);

        if (conversations.Count > MaximumConversations)
        {
            throw EvaluationException.ConversationLimitExceeded($"{conversations.Count} conversations were supplied, and a test set holds at most {MaximumConversations}.");
        }

        foreach (var conversation in conversations)
        {
            if (conversation.Exchanges.Count == 0)
            {
                throw EvaluationException.ConversationLimitExceeded($"Conversation {conversation.Number} has no questions.");
            }

            if (conversation.Exchanges.Count > MaximumExchangesPerConversation)
            {
                throw EvaluationException.ConversationLimitExceeded($"Conversation {conversation.Number} has {conversation.Exchanges.Count} question-and-answer pairs, and the limit is {MaximumExchangesPerConversation}.");
            }

            if (conversation.Exchanges.FirstOrDefault(exchange => exchange.Question.Length > MaximumQuestionLength) is { } tooLong)
            {
                throw EvaluationException.ConversationLimitExceeded($"A question in conversation {conversation.Number} is {tooLong.Question.Length} characters, and the limit is {MaximumQuestionLength}.");
            }
        }
    }

    /// <summary>
    /// Rejects a write that would leave a test set holding more conversations than it may.
    /// </summary>
    /// <param name="stored">How many conversations the set already holds and keeps.</param>
    /// <param name="incoming">How many conversations are being added.</param>
    /// <param name="name">The test set being written to.</param>
    public static void ValidateTotal(int stored, int incoming, string name)
    {
        if (stored + incoming > MaximumConversations)
        {
            throw EvaluationException.ConversationLimitExceeded(
                $"Test set '{name}' holds {stored} conversation(s) and {incoming} more were supplied, which is over the {MaximumConversations} a test set holds.");
        }
    }
}
