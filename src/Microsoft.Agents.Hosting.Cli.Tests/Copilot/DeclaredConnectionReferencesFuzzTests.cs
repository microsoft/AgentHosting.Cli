using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

/// <summary>
/// Deterministic FUZZ-1 boundary corpus for the connection-reference declaration surface.
/// </summary>
/// <remarks>
/// <para>
/// Reading declarations widened from one well-known <c>connectionreferences.mcs.yml</c> to every
/// <c>infrastructure/connections/*.sync.yaml</c> a workspace holds, so the structured YAML parser now
/// runs over an open set of files whose names and content the workspace, not this CLI, decides. This
/// corpus exercises that boundary with hostile and degenerate input.
/// </para>
/// <para>
/// The invariant is that the reader is total over arbitrary bytes: every input either parses to
/// declarations or raises the typed <c>connection-references-unreadable</c> failure naming the file.
/// No other exception may escape, because anything else reaches the operator as an untyped crash and
/// tells an automated caller nothing it can branch on.
/// </para>
/// <para>
/// Continuous mutation-based fuzzing of this parser is tracked in
/// <see href="https://microsoft.ghe.com/bic/agents-managed-hosting/issues/109"/>. This is bounded
/// deterministic regression coverage, not a SharpFuzz or OneFuzz harness, and no hosted fuzzing is
/// provisioned here.
/// </para>
/// </remarks>
public sealed class DeclaredConnectionReferencesFuzzTests : IDisposable
{
    private readonly string _workspacePath = Path.Combine(Path.GetTempPath(), "ah-declared-fuzz-" + Guid.NewGuid().ToString("N"));

    public DeclaredConnectionReferencesFuzzTests() => Directory.CreateDirectory(_workspacePath);

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
    }

    private string Write(string content, string fileName = "probe.sync.yaml")
    {
        var folder = Path.Combine(_workspacePath, "infrastructure", "connections");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, content);

        return path;
    }

    private static string Nested(int depth) =>
        "connectionReferences:" + string.Concat(Enumerable.Range(0, depth).Select(level => $"\n{new string(' ', level * 2)}- a:"));

    private static string AliasChain(int depth, int width)
    {
        var lines = new List<string> { "seed: &a0 [x]" };

        for (var level = 1; level <= depth; level++)
        {
            lines.Add($"level{level}: &a{level} [{string.Join(", ", Enumerable.Repeat($"*a{level - 1}", width))}]");
        }

        lines.Add($"connectionReferences: *a{depth}");

        return string.Join("\n", lines);
    }

    public static TheoryData<string> MalformedDeclarations()
    {
        var data = new TheoryData<string>
        {
            string.Empty,
            "   ",
            "\0",
            "\uFFFF\uFFFE",
            "\ufeffconnectionReferences:\n  - connectionReferenceLogicalName: cr_bom",
            "not yaml at all: [unclosed",
            "connectionReferences: [",
            "connectionReferences:\n\t- connectionReferenceLogicalName: cr_tab",
            "connectionReferences: 5",
            "connectionReferences: \"text\"",
            "connectionReferences: {}",
            "connectionReferences:\n  - 5",
            "connectionReferences:\n  - null",
            "connectionReferences:\n  - connectionReferenceLogicalName:",
            "connectionReferences:\n  - connectionReferenceLogicalName: \"\"",
            "connectionReferences:\n  - connectionReferenceLogicalName: \"   \"",
            "connectionReferences:\n  - connectorId: /providers/Microsoft.PowerApps/apis/shared_teams",
            "connectionReferences:\n  - connectionReferenceLogicalName: cr_a\n  - connectionReferenceLogicalName: cr_a",
            "connectionReferences:\n  - connectionReferenceLogicalName: cr_a\n    connectionReferenceLogicalName: cr_b",
            "connectionReferences:\n  - connectionReferenceLogicalName: " + new string('n', 200_000),
            "connectionReferences:\n  - connectionReferenceLogicalName: \"cr_\\uD800\"",
            "connectionReferences:\n  - connectionReferenceLogicalName: \"cr_\\u0000\"",
            "connectionReferences:\n  - connectionReferenceLogicalName: cr_cr\r    connectorId: x",
            "connectionReferences:\n  - connectionReferenceLogicalName: !!python/object/apply:os.system [echo]",
            "--- \n--- \nconnectionReferences:\n  - connectionReferenceLogicalName: cr_two_docs",
            "connectionReferences:\n  - connectionReferenceLogicalName: cr_a\nconnectionReferences:\n  - connectionReferenceLogicalName: cr_b",
            Nested(256),
            AliasChain(depth: 6, width: 3),
        };

        return data;
    }

    [Theory]
    [MemberData(nameof(MalformedDeclarations))]
    public void ADeclarationFileIsEitherReadOrTypedAsUnreadable(string content)
    {
        var path = Write(content);

        try
        {
            foreach (var reference in DeclaredConnectionReferences.Declared(_workspacePath))
            {
                Assert.False(string.IsNullOrWhiteSpace(reference.ConnectionReferenceLogicalName.Value));
            }
        }
        catch (CopilotStudioException failure)
        {
            Assert.Equal(CopilotErrorKinds.ConnectionReferencesUnreadable, failure.ErrorKind);
            Assert.Equal(ExitCodes.Usage, failure.ExitCode);
            Assert.Contains(path, failure.Message, StringComparison.Ordinal);
            Assert.NotNull(failure.Remediation);
        }
    }

    [Theory]
    [MemberData(nameof(MalformedDeclarations))]
    public void ADeclarationFileNeverEchoesItsWholeContent(string content)
    {
        Write(content);

        try
        {
            DeclaredConnectionReferences.Declared(_workspacePath);
        }
        catch (CopilotStudioException failure)
        {
            Assert.DoesNotContain(new string('n', 1_000), failure.Message, StringComparison.Ordinal);
            Assert.True(failure.Message.Length < 4_000);
        }
    }

    [Fact]
    public void AFileNameTheWorkspaceChoseIsNeverTreatedAsAPattern()
    {
        Write("connectionReferences:\n  - connectionReferenceLogicalName: cr_odd", "a b.[1]{2}(3)+#%.sync.yaml");

        Assert.Equal(
            ["cr_odd"],
            DeclaredConnectionReferences.Declared(_workspacePath).Select(reference => reference.ConnectionReferenceLogicalName.Value));
    }

    [Fact]
    public void AFileThatIsNotADeclarationIsNotRead()
    {
        var folder = Path.Combine(_workspacePath, "infrastructure", "connections");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not yaml at all: [unclosed");
        File.WriteAllText(Path.Combine(folder, "nested.sync.yaml.bak"), "not yaml at all: [unclosed");

        Assert.Empty(DeclaredConnectionReferences.Declared(_workspacePath));
    }

    [Fact]
    public void OneUnreadableFileIsNamedRatherThanItsReadableNeighbours()
    {
        Write("connectionReferences:\n  - connectionReferenceLogicalName: cr_fine", "a.sync.yaml");
        var broken = Write("connectionReferences:\n  - connectionReferenceLogicalName:", "b.sync.yaml");

        var failure = Assert.Throws<CopilotStudioException>(() => DeclaredConnectionReferences.Declared(_workspacePath));

        Assert.Contains(broken, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cr_fine", failure.Message, StringComparison.Ordinal);
    }
}
