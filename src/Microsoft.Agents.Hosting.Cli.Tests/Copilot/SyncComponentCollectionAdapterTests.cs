using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncComponentCollectionAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ah-collections-" + Guid.NewGuid().ToString("N"));

    private readonly SyncComponentCollectionAdapter _collections = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Nested(string name, string fileName)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), "schemaName: con_agent\n");

        return directory;
    }

    private string AgentDeclaring(params string[] directories)
    {
        var agent = Nested("Contoso Helpdesk", WorkspaceFileNames.Settings);
        File.WriteAllText(Path.Combine(agent, WorkspaceFileNames.References), "componentCollections:\n" + string.Concat(directories.Select(directory => $"  - schemaName:\n    directory: {directory}\n")));

        return agent;
    }

    [Fact]
    public void Resolve_FindsTheCollectionsTheWorkspaceDeclares()
    {
        var agent = Nested("Contoso Helpdesk", WorkspaceFileNames.Settings);
        var first = Nested("Alpha Tools", WorkspaceFileNames.Collection);
        var second = Nested("Zeta Tools", WorkspaceFileNames.Collection);
        File.WriteAllText(
            Path.Combine(agent, WorkspaceFileNames.References),
            "componentCollections:\n  - schemaName:\n    directory: ../Alpha Tools/\n  - schemaName:\n    directory: ../Zeta Tools/\n");

        Assert.Equal([first, second], _collections.Resolve(_root, agent).Found);
    }

    [Fact]
    public void Resolve_IgnoresUndeclaredSiblings()
    {
        var agent = Nested("Contoso Helpdesk", WorkspaceFileNames.Settings);
        Nested("Alpha Tools", WorkspaceFileNames.Collection);

        Assert.Empty(_collections.Resolve(_root, agent).Found);
    }

    [Fact]
    public void Resolve_ReportsCollectionsThatDoNotExistAsMissing()
    {
        var agent = Nested("Contoso Helpdesk", WorkspaceFileNames.Settings);
        File.WriteAllText(
            Path.Combine(agent, WorkspaceFileNames.References),
            "componentCollections:\n  - schemaName:\n    directory: ../Absent Tools/\n");

        var collections = _collections.Resolve(_root, agent);

        Assert.Empty(collections.Found);
        Assert.Equal(["../Absent Tools/"], collections.Missing);
    }

    [Fact]
    public void Resolve_ReportsCollectionsOutsideTheProjectAsMissing()
    {
        var agent = Nested("Contoso Helpdesk", WorkspaceFileNames.Settings);
        File.WriteAllText(
            Path.Combine(agent, WorkspaceFileNames.References),
            "componentCollections:\n  - schemaName:\n    directory: ../../Escaped Tools/\n");

        var collections = _collections.Resolve(_root, agent);

        Assert.Empty(collections.Found);
        Assert.Equal(["../../Escaped Tools/"], collections.Missing);
    }

    [Fact]
    public void Resolve_ReportsACollectionReachedThroughAJunctionInThePathAsMissing()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"ah-outside-{Guid.NewGuid():N}");
        var inner = Path.Combine(outside, "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, WorkspaceFileNames.Collection), string.Empty);

        try
        {
            var agent = Nested("Contoso Helpdesk", WorkspaceFileNames.Settings);
            Directory.CreateSymbolicLink(Path.Combine(_root, "Linked Tools"), outside);
            File.WriteAllText(
                Path.Combine(agent, WorkspaceFileNames.References),
                "componentCollections:\n  - schemaName:\n    directory: ../Linked Tools/inner/\n");

            var collections = _collections.Resolve(_root, agent);

            Assert.Empty(collections.Found);
            Assert.Equal(["../Linked Tools/inner/"], collections.Missing);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ReportsACollectionReachedThroughAJunctionOutOfTheProjectAsMissing()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"ah-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, WorkspaceFileNames.Collection), string.Empty);

        try
        {
            var agent = Nested("Contoso Helpdesk", WorkspaceFileNames.Settings);
            Directory.CreateSymbolicLink(Path.Combine(_root, "Linked Tools"), outside);
            File.WriteAllText(
                Path.Combine(agent, WorkspaceFileNames.References),
                "componentCollections:\n  - schemaName:\n    directory: ../Linked Tools/\n");

            var collections = _collections.Resolve(_root, agent);

            Assert.Empty(collections.Found);
            Assert.Equal(["../Linked Tools/"], collections.Missing);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Resolve_FindsACollectionReachedThroughALinkThatStaysInsideTheProject()
    {
        var target = Nested("Alpha Tools", WorkspaceFileNames.Collection);
        var agent = Nested("Contoso Helpdesk", WorkspaceFileNames.Settings);
        var linked = Path.Combine(_root, "Linked Tools");
        Directory.CreateSymbolicLink(linked, target);
        File.WriteAllText(
            Path.Combine(agent, WorkspaceFileNames.References),
            "componentCollections:\n  - schemaName:\n    directory: ../Linked Tools/\n");

        var collections = _collections.Resolve(_root, agent);

        Assert.Equal([linked], collections.Found);
        Assert.Empty(collections.Missing);
    }

    [Fact]
    public void Resolve_InAMissingDirectoryFindsNothing()
    {
        Assert.Empty(_collections.Resolve(_root, Path.Combine(_root, "absent")).Found);
    }

    [Fact]
    public void Resolve_AnUnparseableReferencesFile_DeclaresNoCollections()
    {
        var agent = Nested("Contoso Helpdesk", WorkspaceFileNames.Settings);
        Nested("Alpha Tools", WorkspaceFileNames.Collection);
        File.WriteAllText(Path.Combine(agent, WorkspaceFileNames.References), "componentCollections: [unclosed\n    directory: ../Alpha Tools/\n");

        var collections = _collections.Resolve(_root, agent);

        Assert.Empty(collections.Found);
        Assert.Empty(collections.Missing);
    }

    [Fact]
    public async Task Push_PushesTheCollectionsTheCallerLetsThroughThenTheAgent()
    {
        var agent = AgentDeclaring("../Alpha Tools/", "../Zeta Tools/");
        var alpha = Nested("Alpha Tools", WorkspaceFileNames.Collection);
        var zeta = Nested("Zeta Tools", WorkspaceFileNames.Collection);
        var pushed = new List<string>();

        var result = await _collections.PushAsync(_root, agent, (directory, _) => { pushed.Add(directory); return Task.FromResult(directory.Length); }, (collection, _) => Task.FromResult(collection == alpha), CancellationToken.None);

        Assert.Equal([alpha, agent], pushed);
        Assert.Equal([(alpha, true, alpha.Length), (zeta, false, 0)], result.Collections.Select(collection => (collection.Directory, collection.Pushed, collection.Result)));
        Assert.Equal(agent.Length, result.Agent);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public async Task Push_ReportsTheDeclarationsThatResolveToNothingAndStillPushesTheAgent()
    {
        var agent = AgentDeclaring("../Absent Tools/");
        var pushed = new List<string>();

        var result = await _collections.PushAsync(_root, agent, (directory, _) => { pushed.Add(directory); return Task.FromResult(0); }, (_, _) => Task.FromResult(true), CancellationToken.None);

        Assert.Equal([agent], pushed);
        Assert.Empty(result.Collections);
        Assert.Equal(["../Absent Tools/"], result.Missing);
    }

    [Fact]
    public async Task Push_StopsAtTheFirstPushThatFails()
    {
        var agent = AgentDeclaring("../Alpha Tools/");
        Nested("Alpha Tools", WorkspaceFileNames.Collection);
        var pushed = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _collections.PushAsync<int>(_root, agent, (directory, _) => { pushed.Add(directory); throw new InvalidOperationException("refused"); }, (_, _) => Task.FromResult(true), CancellationToken.None));

        Assert.DoesNotContain(agent, pushed);
    }
}