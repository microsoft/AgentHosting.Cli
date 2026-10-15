using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Evaluations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Evaluation;

public sealed class EvaluationCsvReaderTests
{
    private const string Path = "conversations.csv";

    private static string Csv(params string[] rows) =>
        string.Join("\n", ["\"conversationNumber\",\"question\",\"response\"", .. rows]);

    [Fact]
    public void Read_GroupsRowsSharingAConversationNumberIntoOneConversation()
    {
        var conversations = EvaluationCsvReader.Read(Csv("\"1\",\"a\",\"\"", "\"1\",\"b\",\"\"", "\"2\",\"c\",\"\""), Path);

        Assert.Equal(2, conversations.Count);
        Assert.Equal(2, conversations[0].Exchanges.Count);
        Assert.Single(conversations[1].Exchanges);
    }

    [Fact]
    public void Read_RenumbersConversationsFromOneInAscendingOrder()
    {
        var conversations = EvaluationCsvReader.Read(Csv("\"7\",\"a\",\"\"", "\"3\",\"b\",\"\""), Path);

        Assert.Equal([1, 2], conversations.Select(conversation => conversation.Number));
        Assert.Equal("b", conversations[0].Exchanges[0].Question);
    }

    [Fact]
    public void Read_KeepsTheOptionalResponseWhenPresent()
    {
        var conversations = EvaluationCsvReader.Read(Csv("\"1\",\"question\",\"reference\""), Path);

        Assert.Equal("reference", conversations[0].Exchanges[0].Response);
    }

    [Fact]
    public void Read_TreatsAnEmptyResponseAsAbsent()
    {
        var conversations = EvaluationCsvReader.Read(Csv("\"1\",\"question\",\"\""), Path);

        Assert.Null(conversations[0].Exchanges[0].Response);
    }

    [Fact]
    public void Read_MarksImportedConversationsAsImported()
    {
        var conversations = EvaluationCsvReader.Read(Csv("\"1\",\"question\",\"\""), Path);

        Assert.Equal(EvaluationConversationSource.Imported, conversations[0].Source);
    }

    [Fact]
    public void Read_IgnoresTheCommentPreambleTheTemplateShipsWith()
    {
        var content = string.Join("\n", ["\"# Import conversations to test your agent.\"", "\"#\"", Csv("\"1\",\"question\",\"\"")]);

        Assert.Single(EvaluationCsvReader.Read(content, Path));
    }

    [Fact]
    public void Read_IgnoresBlankLines()
    {
        Assert.Single(EvaluationCsvReader.Read(Csv("", "\"1\",\"question\",\"\"", ""), Path));
    }

    [Fact]
    public void Read_AcceptsCarriageReturnLineEndings()
    {
        var content = "\"conversationNumber\",\"question\",\"response\"\r\n\"1\",\"question\",\"\"\r\n";

        Assert.Single(EvaluationCsvReader.Read(content, Path));
    }

    [Fact]
    public void Read_AcceptsAByteOrderMark()
    {
        Assert.Single(EvaluationCsvReader.Read("\uFEFF" + Csv("\"1\",\"question\",\"\""), Path));
    }

    [Fact]
    public void Read_KeepsCommasInsideQuotedFields()
    {
        var conversations = EvaluationCsvReader.Read(Csv("\"1\",\"one, two, three\",\"\""), Path);

        Assert.Equal("one, two, three", conversations[0].Exchanges[0].Question);
    }

    [Fact]
    public void Read_UnescapesDoubledQuotes()
    {
        var conversations = EvaluationCsvReader.Read(Csv("\"1\",\"say \"\"hello\"\" twice\",\"\""), Path);

        Assert.Equal("say \"hello\" twice", conversations[0].Exchanges[0].Question);
    }

    [Fact]
    public void Read_KeepsNewlinesInsideQuotedFields()
    {
        var conversations = EvaluationCsvReader.Read(Csv("\"1\",\"first\nsecond\",\"\""), Path);

        Assert.Equal("first\nsecond", conversations[0].Exchanges[0].Question);
    }

    [Fact]
    public void Read_AcceptsColumnsInAnyOrder()
    {
        var content = "\"question\",\"response\",\"conversationNumber\"\n\"asked\",\"\",\"1\"";
        var conversations = EvaluationCsvReader.Read(content, Path);

        Assert.Equal("asked", conversations[0].Exchanges[0].Question);
    }

    [Fact]
    public void Read_AcceptsAFileWithoutAResponseColumn()
    {
        var content = "\"conversationNumber\",\"question\"\n\"1\",\"asked\"";

        Assert.Null(EvaluationCsvReader.Read(content, Path)[0].Exchanges[0].Response);
    }

    [Fact]
    public void Read_KeepsAQuestionThatOpensWithAHash()
    {
        var conversations = EvaluationCsvReader.Read(Csv("\"1\",\"# Summarize the document\",\"\""), Path);

        Assert.Equal("# Summarize the document", Assert.Single(conversations).Exchanges[0].Question);
    }

    [Fact]
    public void Read_KeepsAHashQuestionWhenItIsTheFirstColumn()
    {
        var content = string.Join(
            "\n",
            ["\"# Import conversations to test your agent.\"", "\"question\",\"response\",\"conversationNumber\"", "\"# Summarize the document\",\"\",\"1\"", "\"Other question\",\"\",\"2\""]);

        var conversations = EvaluationCsvReader.Read(content, Path);

        Assert.Equal(2, conversations.Count);
        Assert.Equal("# Summarize the document", conversations[0].Exchanges[0].Question);
        Assert.Equal("Other question", conversations[1].Exchanges[0].Question);
    }

    [Fact]
    public void Read_RejectsAQuotedValueThatIsNeverClosed()
    {
        var failure = Assert.Throws<EvaluationException>(() => EvaluationCsvReader.Read("conversationNumber,question,response\n1,\"first\n2,second,", Path));

        Assert.Equal(EvaluationErrorKinds.ConversationFileInvalid, failure.ErrorKind);
        Assert.Contains("never closed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsAQuoteInsideAnUnquotedValue()
    {
        var failure = Assert.Throws<EvaluationException>(() => EvaluationCsvReader.Read("conversationNumber,question,response\n1,What does \"foo\" mean?,", Path));

        Assert.Equal(EvaluationErrorKinds.ConversationFileInvalid, failure.ErrorKind);
        Assert.Contains("outside a quoted value", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsARowTooShortForTheConversationNumberColumn()
    {
        var failure = Assert.Throws<EvaluationException>(() => EvaluationCsvReader.Read("\"question\",\"response\",\"conversationNumber\"\n\"Hello\"", Path));

        Assert.Equal(EvaluationErrorKinds.ConversationFileInvalid, failure.ErrorKind);
        Assert.Contains("1 column(s)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsAHeaderMissingTheQuestionColumn()
    {
        var failure = Assert.Throws<EvaluationException>(() => EvaluationCsvReader.Read("\"conversationNumber\",\"answer\"\n\"1\",\"x\"", Path));

        Assert.Equal(EvaluationErrorKinds.ConversationFileInvalid, failure.ErrorKind);
    }

    [Fact]
    public void Read_RejectsANonNumericConversationNumber()
    {
        Assert.Throws<EvaluationException>(() => EvaluationCsvReader.Read(Csv("\"first\",\"question\",\"\""), Path));
    }

    [Fact]
    public void Read_RejectsARowWithNoQuestion()
    {
        Assert.Throws<EvaluationException>(() => EvaluationCsvReader.Read(Csv("\"1\",\"\",\"\""), Path));
    }

    [Fact]
    public void Read_RejectsAFileHoldingOnlyAHeader()
    {
        Assert.Throws<EvaluationException>(() => EvaluationCsvReader.Read(Csv(), Path));
    }

    [Fact]
    public void Read_RejectsAnEmptyFile()
    {
        Assert.Throws<EvaluationException>(() => EvaluationCsvReader.Read(string.Empty, Path));
    }

    [Fact]
    public void Read_RejectsMoreConversationsThanTheServiceAccepts()
    {
        var rows = Enumerable.Range(1, EvaluationConversationLimits.MaximumConversations + 1).Select(number => $"\"{number}\",\"question\",\"\"").ToArray();
        var failure = Assert.Throws<EvaluationException>(() => EvaluationCsvReader.Read(Csv(rows), Path));

        Assert.Equal(EvaluationErrorKinds.ConversationLimitExceeded, failure.ErrorKind);
    }

    [Fact]
    public void Read_RejectsMoreExchangesThanOneConversationMayHold()
    {
        var rows = Enumerable.Range(1, EvaluationConversationLimits.MaximumExchangesPerConversation + 1).Select(_ => "\"1\",\"question\",\"\"").ToArray();
        var failure = Assert.Throws<EvaluationException>(() => EvaluationCsvReader.Read(Csv(rows), Path));

        Assert.Equal(EvaluationErrorKinds.ConversationLimitExceeded, failure.ErrorKind);
    }

    [Fact]
    public void Read_RejectsAQuestionLongerThanTheServiceAccepts()
    {
        var question = new string('x', EvaluationConversationLimits.MaximumQuestionLength + 1);
        var failure = Assert.Throws<EvaluationException>(() => EvaluationCsvReader.Read(Csv($"\"1\",\"{question}\",\"\""), Path));

        Assert.Equal(EvaluationErrorKinds.ConversationLimitExceeded, failure.ErrorKind);
    }

    [Fact]
    public void Read_AcceptsAQuestionAtExactlyTheLimit()
    {
        var question = new string('x', EvaluationConversationLimits.MaximumQuestionLength);

        Assert.Single(EvaluationCsvReader.Read(Csv($"\"1\",\"{question}\",\"\""), Path));
    }
}

public sealed class EvaluationCsvWriterTests
{
    private static EvaluationConversation Conversation(int number, params EvaluationExchange[] exchanges) =>
        new() { Number = number, Exchanges = exchanges };

    [Fact]
    public void Write_RoundTripsThroughTheReader()
    {
        EvaluationConversation[] original =
        [
            Conversation(1, new EvaluationExchange("first", "reply"), new EvaluationExchange("second", null)),
            Conversation(2, new EvaluationExchange("third", "answer")),
        ];

        var restored = EvaluationCsvReader.Read(EvaluationCsvWriter.Write(original), "round-trip.csv");

        Assert.Equal(2, restored.Count);
        Assert.Equal(["first", "second"], restored[0].Exchanges.Select(exchange => exchange.Question));
        Assert.Equal("reply", restored[0].Exchanges[0].Response);
        Assert.Null(restored[0].Exchanges[1].Response);
        Assert.Equal("third", restored[1].Exchanges[0].Question);
    }

    [Fact]
    public void Write_RoundTripsTextHoldingCommasQuotesAndNewlines()
    {
        var awkward = "a, b \"quoted\"\nnext line";
        var restored = EvaluationCsvReader.Read(EvaluationCsvWriter.Write([Conversation(1, new EvaluationExchange(awkward, awkward))]), "awkward.csv");

        Assert.Equal(awkward, restored[0].Exchanges[0].Question);
        Assert.Equal(awkward, restored[0].Exchanges[0].Response);
    }

    [Fact]
    public void Write_EmitsTheTemplateCommentPreamble()
    {
        var written = EvaluationCsvWriter.Write([Conversation(1, new EvaluationExchange("question", null))]);

        Assert.StartsWith("\"# Import conversations to test your agent.\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_EmitsTheTemplateHeaderRow()
    {
        var written = EvaluationCsvWriter.Write([Conversation(1, new EvaluationExchange("question", null))]);

        Assert.Contains("\"conversationNumber\",\"question\",\"response\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RoundTripsAQuestionThatOpensWithAHash()
    {
        var written = EvaluationCsvWriter.Write([Conversation(1, new EvaluationExchange("# Summarize the document", null))]);

        Assert.Equal("# Summarize the document", EvaluationCsvReader.Read(written, "hash.csv")[0].Exchanges[0].Question);
    }
}

public sealed class CsvDocumentTests
{
    [Fact]
    public void ReadRows_RefusesAQuotedValueThatIsNeverClosed()
    {
        Assert.Throws<FormatException>(() => CsvDocument.ReadRows("a,\"unterminated\nb,c"));
    }

    [Fact]
    public void ReadRows_RefusesAQuoteInsideAnUnquotedValue()
    {
        Assert.Throws<FormatException>(() => CsvDocument.ReadRows("a,b\"c"));
    }

    [Fact]
    public void ReadRows_AcceptsAQuotedValueClosedAtTheEnd()
    {
        var rows = CsvDocument.ReadRows("a,\"terminated\"");

        Assert.Equal(["a", "terminated"], Assert.Single(rows));
    }

    [Theory]
    [InlineData("a,\"first\"second")]
    [InlineData("a,\"first\"second,c")]
    [InlineData("a,\"first\" \nb,c")]
    public void ReadRows_RefusesTextAfterAClosingQuote(string content)
    {
        var failure = Assert.Throws<FormatException>(() => CsvDocument.ReadRows(content));

        Assert.Contains("closing quote", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadRows_AcceptsAQuotedValueFollowedByASeparatorOrNewline()
    {
        var rows = CsvDocument.ReadRows("\"a\",\"b\"\r\n\"c\",\"d\"");

        Assert.Equal([["a", "b"], ["c", "d"]], rows);
    }
}
