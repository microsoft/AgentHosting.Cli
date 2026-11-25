using System.Text;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class MsalDiagnosticWriterTests
{
    private const string Context = "MSAL 4.92.0.0 MSAL.NetCore .NET 10.0.15 Microsoft Windows 10.0.26300";
    private const string FirstId = "1e45fd73-adbf-4b7a-993a-e6f9a022cd30";
    private const string SecondId = "59DE06FD-5856-40D0-ACD4-C53C1F44781E";
    private const string SdkTime = "2026-11-20 04:18:15Z";
    private const string DisplayTime = "2026-11-20 04:18:15.000";

    [Fact]
    public void SharedContextDoesNotSuppressEventsOrInventCorrelation()
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output);

        writer.Write(LogLevel.Info, SdkMessage("Account id filter: False", FirstId), false);
        writer.Write(LogLevel.Info, SdkMessage("IsLegacyAdalCacheEnabled: yes", FirstId), false);
        writer.Write(LogLevel.Info, SdkMessage("IsLegacyAdalCacheEnabled: yes", FirstId), false);
        writer.Write(LogLevel.Always, SdkMessage("[Internal cache] Clearing user token cache accessor."), false);
        writer.Write(LogLevel.Info, SdkMessage("ForceRefresh: False", SecondId), false);

        Assert.Equal(string.Join(Environment.NewLine,
            $"{DisplayTime} [MSAL] False {Context}",
            $"{DisplayTime} - {FirstId} [MSAL] Account id filter: False",
            $"{DisplayTime} - {FirstId} [MSAL] IsLegacyAdalCacheEnabled: yes",
            $"{DisplayTime} - {FirstId} [MSAL] IsLegacyAdalCacheEnabled: yes",
            $"{DisplayTime} [MSAL] [Internal cache] Clearing user token cache accessor.",
            $"{DisplayTime} - {SecondId} [MSAL] ForceRefresh: False",
            ""), output.ToString());
        Assert.DoesNotContain(Environment.NewLine + Environment.NewLine, output.ToString());
    }

    [Theory]
    [InlineData("True")]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void LeadingBooleanIsPreservedAsContextWithoutInterpretation(string boolean)
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output);

        writer.Write(LogLevel.Info, $"{boolean} SDK context [{SdkTime}] body", false);

        Assert.Equal($"{DisplayTime} [MSAL] {boolean} SDK context{Environment.NewLine}" +
            $"{DisplayTime} [MSAL] body{Environment.NewLine}", output.ToString());
    }

    [Theory]
    [InlineData("MSAL SDK context")]
    [InlineData("Falsehood SDK context")]
    [InlineData("Unknown SDK context")]
    [InlineData("")]
    public void MissingBooleanStillSplitsMetadataWithoutGuessingPiiLogging(string context)
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output);

        writer.Write(LogLevel.Warning, $"{context}[{SdkTime} - {FirstId}] body", false);

        Assert.Equal($"{DisplayTime} [MSAL] {context}{Environment.NewLine}" +
            $"{DisplayTime} - {FirstId} [MSAL] body{Environment.NewLine}", output.ToString());
        Assert.DoesNotContain("PII logging:", output.ToString());
    }

    [Fact]
    public void FirstValidMetadataSkipsOtherBracketsAndLeavesLaterMetadataInTheBody()
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output);
        const string before = "SDK [other context] [2026-99-20 04:18:15Z] [2026-11-20 04:18:15Z - not-a-guid]";
        const string body = " (client: 1.0) body [2026-11-21 04:18:15Z]";

        writer.Write(LogLevel.Info, $"False {before} [{SdkTime} - {FirstId}]{body}", false);

        Assert.Equal($"{DisplayTime} [MSAL] False {before}{Environment.NewLine}" +
            $"{DisplayTime} - {FirstId} [MSAL]{body}{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void ContextAndConvertedSdkTimestampsRemainVisibleWhenTheyChange()
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output);

        writer.Write(LogLevel.Info, SdkMessage("first"), false);
        writer.Write(LogLevel.Info, $"True other SDK runtime OS [2026-11-20 04:18:16Z] second", true);
        writer.Write(LogLevel.Info, SdkMessage("third"), false);

        var text = output.ToString();
        Assert.Contains("2026-11-20 04:18:16.000 [MSAL] True other SDK runtime OS", text);
        Assert.Equal(2, text.Split($"{DisplayTime} [MSAL] False {Context}").Length - 1);
        Assert.Contains("2026-11-20 04:18:16.000 [MSAL] second", text);
        Assert.EndsWith($"{DisplayTime} [MSAL] third{Environment.NewLine}", text);
    }

    [Theory]
    [InlineData(LogLevel.Info, true)]
    [InlineData(LogLevel.Warning, true)]
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Always, true)]
    [InlineData(LogLevel.Verbose, true)]
    [InlineData(LogLevel.Error, false)]
    public void OnlyMetadataIsGrayAndInteriorWhitespaceIsUntouched(LogLevel level, bool color)
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output, color);
        const string body = " (client: 1.0) \r\n=== Request Data ===\n  Scopes - resource//.default\rField: \r\n\r\n";

        writer.Write(level, $"False {Context} [{SdkTime} - {SecondId}]{body}", false);

        var timestamp = color ? $"\u001b[90m{DisplayTime}\u001b[0m" : DisplayTime;
        var metadata = $"{DisplayTime} - {SecondId}";
        if (color)
        {
            metadata = $"\u001b[90m{metadata}\u001b[0m";
        }

        Assert.Equal($"{timestamp} [MSAL] False {Context}{Environment.NewLine}" +
            $"{metadata} [MSAL] (client: 1.0) \r\n=== Request Data ===\n  Scopes - resource//.default\rField:{Environment.NewLine}",
            output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataWithoutCorrelationShowsOnlyTheConvertedSdkTimestamp(bool color)
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output, color);

        writer.Write(LogLevel.Always, SdkMessage("TokenEndpoint: ****"), false);

        var metadata = DisplayTime;
        if (color)
        {
            metadata = $"\u001b[90m{metadata}\u001b[0m";
        }

        Assert.EndsWith($"{metadata} [MSAL] TokenEndpoint: ****{Environment.NewLine}", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unprefixed SDK message")]
    [InlineData("")]
    [InlineData("False MSAL future SDK [unrecognized metadata] keep everything")]
    [InlineData("False MSAL runtime OS [2026-99-20 04:18:15Z] invalid date")]
    [InlineData("False MSAL runtime OS [2026-11-20 04:18:15Z - not-a-guid] invalid ID")]
    [InlineData("False MSAL runtime OS [2026-11-20 04:18:15Z")]
    [InlineData("False MSAL runtime OS [2026-11-20 04:18:15Z - 59de06fd585640d0acd4c53c1f44781e] unsupported ID format")]
    public void MissingValidMetadataKeepsTheOriginalMessageWithoutAddingMetadataOrSourceTags(string message)
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output, color: true);

        writer.Write(LogLevel.Warning, message, false);

        Assert.Equal(message.Length == 0 ? "" : message + Environment.NewLine, output.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("body without a separating space")]
    [InlineData("body\r\n\tcontinuation with original indentation")]
    public void NonemptyBodiesHaveOneSeparatorAfterMetadataEvenWithoutAnOriginalDelimiter(string body)
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output);

        writer.Write(LogLevel.Info, $"False {Context} [{SdkTime}]{body}", false);

        var separator = body.Length == 0 ? "" : " ";
        Assert.EndsWith($"{DisplayTime} [MSAL]{separator}{body}{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void SeparateCallbacksAreNotGroupedOrRewrittenByWording()
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output);
        var messages = new[]
        {
            "==== GetAccounts started - GetAccounts ====",
            $"MSAL MSAL.NetCore with assembly version '4.92.0.0'. CorrelationId({FirstId})",
            "=== AcquireTokenSilent Parameters ===",
            "LoginHint provided: False",
            "ForceRefresh: False",
            "=== Token Acquisition finished successfully:",
            "AT expiration time: expiry, scopes: resource//.default. source: IdentityProvider",
            "Fetched access token from host login.microsoftonline.com.",
        };

        foreach (var message in messages)
        {
            writer.Write(LogLevel.Info, SdkMessage(message, FirstId), false);
        }

        Assert.Equal($"{DisplayTime} [MSAL] False {Context}{Environment.NewLine}" +
            string.Concat(messages.Select(message => $"{DisplayTime} - {FirstId} [MSAL] {message}{Environment.NewLine}")),
            output.ToString());
    }

    [Fact]
    public void TrimmingOuterWhitespacePreservesInteriorBlankLinesAndIndentation()
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output, color: true);

        writer.Write(LogLevel.Info, SdkMessage(
            "\r\n \t\r\n\t=== Request Data ===  \r\n  Authority Provided? - True\n \t\n\tScopes - resource/.default  \r\n\t \r\n", FirstId), false);

        Assert.EndsWith($"\u001b[90m{DisplayTime} - {FirstId}\u001b[0m [MSAL] " +
            "=== Request Data ===  \r\n  Authority Provided? - True\n \t\n\tScopes - resource/.default" +
            Environment.NewLine, output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OuterSpacesAndTabsAreRemovedWithoutColoringTheBody(bool color)
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output, color);

        writer.Write(LogLevel.Info, SdkMessage(" \t  message \t ", FirstId), false);

        var metadata = $"{DisplayTime} - {FirstId}";
        if (color)
        {
            metadata = $"\u001b[90m{metadata}\u001b[0m";
        }

        Assert.EndsWith($"{metadata} [MSAL] message{Environment.NewLine}", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void RequestDataAppearsBesideMetadataAfterRemovingBlankBoundaryLines(string newline)
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output);

        writer.Write(LogLevel.Info, SdkMessage(
            newline + newline + "=== Request Data ===" + newline + "Scopes - resource/.default" + newline + newline, FirstId), false);

        Assert.EndsWith($"{DisplayTime} - {FirstId} [MSAL] === Request Data ==={newline}Scopes - resource/.default{Environment.NewLine}",
            output.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData(" \r\n\t\n\r")]
    public void BlankOnlyBodiesKeepMetadataWithoutExtraEmptyLines(string body)
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output);

        writer.Write(LogLevel.Info, SdkMessage(body, FirstId), false);

        Assert.EndsWith($"{DisplayTime} - {FirstId} [MSAL]{Environment.NewLine}", output.ToString());
        writer.Write(LogLevel.Warning, body, false);
        Assert.EndsWith($"{DisplayTime} - {FirstId} [MSAL]{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void UnrecognizedEnvelopeAlsoTrimsOnlyOuterWhitespace()
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output, color: true);

        writer.Write(LogLevel.Error, "\n \t\r\n\tunknown message  \r\n\r\n  detail \n \t\r\n", false);

        Assert.Equal("unknown message  \r\n\r\n  detail" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void ConcurrentCallbacksWriteWholeEventsAndShareContext()
    {
        var output = new StringWriter();
        var writer = new MsalDiagnosticWriter(output);

        Parallel.For(0, 64, index => writer.Write(LogLevel.Info, SdkMessage($"event {index}\nend {index}", FirstId), false));

        var text = output.ToString();
        Assert.Equal(1, text.Split($"[MSAL] False {Context}").Length - 1);
        Assert.Equal(1 + 64 * 2, text.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries).Length);
        for (var index = 0; index < 64; index++)
        {
            Assert.Contains($"{DisplayTime} - {FirstId} [MSAL] event {index}\nend {index}{Environment.NewLine}", text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableDiagnosticsAreRecordedWithoutThrowing(bool disposed)
    {
        Exception failure = disposed ? new ObjectDisposedException("stderr") : new IOException("unavailable stderr");
        Exception? observed = null;
        var writer = new MsalDiagnosticWriter(new UnavailableWriter(failure), recordWriteError: error => observed = error);

        writer.Write(LogLevel.Info, SdkMessage("callback"), false);

        Assert.Same(failure, writer.DiagnosticWriteError);
        Assert.Same(failure, observed);
    }

    private sealed class UnavailableWriter(Exception failure) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(string? value) => throw failure;
    }

    private static string SdkMessage(string message, string? correlationId = null) =>
        $"False {Context} [{SdkTime}{(correlationId is null ? "" : $" - {correlationId}")}] {message}";
}
