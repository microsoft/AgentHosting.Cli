using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class DeclaredConnectionReferencesTests : IDisposable
{
    private readonly string _workspacePath = Path.Combine(Path.GetTempPath(), "ah-declared-refs-" + Guid.NewGuid().ToString("N"));

    public DeclaredConnectionReferencesTests() => Directory.CreateDirectory(_workspacePath);

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
    }

    private void WriteClassic(string logicalName, string connectorInternalId) =>
        File.WriteAllText(
            Path.Combine(_workspacePath, "connectionreferences.mcs.yml"),
            $"""
            connectionReferences:
              - connectionReferenceLogicalName: {logicalName}
                connectorId: /providers/Microsoft.PowerApps/apis/{connectorInternalId}
            """);

    private void WriteCli(string logicalName, string connectorInternalId)
    {
        var folder = Path.Combine(_workspacePath, "infrastructure", "connections");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, $"{logicalName}.sync.yaml"),
            $"""
            connectionReferences:
              - connectionReferenceLogicalName: {logicalName}
                connectorId: /providers/Microsoft.PowerApps/apis/{connectorInternalId}
            """);
    }

    private static ConnectionReference Reference(string logicalName) =>
        new ConnectionReference.Builder
        {
            ConnectionReferenceLogicalName = new ConnectionReferenceLogicalName(logicalName),
        }.Build();

    private static IReadOnlyList<string> Names(ImmutableArray<ConnectionReference> references) =>
        [.. references.Select(reference => reference.ConnectionReferenceLogicalName.Value)];

    [Fact]
    public void Declared_ReadsTheCliLayout()
    {
        WriteCli("new_sharedteams_777c895a", "shared_teams");

        Assert.Equal(["new_sharedteams_777c895a"], Names(DeclaredConnectionReferences.Declared(_workspacePath)));
    }

    [Fact]
    public void Declared_ReadsTheClassicLayout()
    {
        WriteClassic("new_sharedmsnweather_08889", "shared_msnweather");

        Assert.Equal(["new_sharedmsnweather_08889"], Names(DeclaredConnectionReferences.Declared(_workspacePath)));
    }

    [Fact]
    public void Declared_ReadsBothLayoutsAndTakesAReferenceDeclaredTwiceOnce()
    {
        WriteClassic("new_sharedteams_777c895a", "shared_teams");
        WriteCli("new_sharedteams_777c895a", "shared_teams");
        WriteCli("crf9a_nb1.cr.shared_sharepointonline.a972", "shared_sharepointonline");

        Assert.Equal(
            ["crf9a_nb1.cr.shared_sharepointonline.a972", "new_sharedteams_777c895a"],
            Names(DeclaredConnectionReferences.Declared(_workspacePath)).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Declared_TakesAReferenceDeclaredTwiceForTheSameConnectorInEitherForm()
    {
        File.WriteAllText(
            Path.Combine(_workspacePath, "connectionreferences.mcs.yml"),
            """
            connectionReferences:
              - connectionReferenceLogicalName: new_sharedteams_777c895a
                connectorId: shared_teams
            """);
        WriteCli("new_sharedteams_777c895a", "shared_teams");

        Assert.Equal(["new_sharedteams_777c895a"], Names(DeclaredConnectionReferences.Declared(_workspacePath)));
    }

    [Fact]
    public void Declared_ADeclarationNamingNoConnectorDefersToOneThatDoes()
    {
        WriteClassic("new_sharedteams_777c895a", "shared_teams");
        var folder = Path.Combine(_workspacePath, "infrastructure", "connections");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "new_sharedteams_777c895a.sync.yaml"),
            """
            connectionReferences:
              - connectionReferenceLogicalName: new_sharedteams_777c895a
            """);

        var declared = DeclaredConnectionReferences.Declared(_workspacePath);

        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_teams", Assert.Single(declared).ConnectorId.Value);
    }

    [Fact]
    public void Declared_AConnectorNamedOnlyByTheSecondDeclarationIsTheOneKept()
    {
        File.WriteAllText(
            Path.Combine(_workspacePath, "connectionreferences.mcs.yml"),
            """
            connectionReferences:
              - connectionReferenceLogicalName: new_sharedteams_777c895a
            """);
        WriteCli("new_sharedteams_777c895a", "shared_teams");

        var declared = DeclaredConnectionReferences.Declared(_workspacePath);

        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_teams", Assert.Single(declared).ConnectorId.Value);
    }

    [Fact]
    public void Declared_ARefusesANameDeclaredForTwoConnectorsRatherThanLettingFileOrderDecide()
    {
        WriteClassic("new_sharedteams_777c895a", "shared_teams");
        WriteCli("new_sharedteams_777c895a", "shared_msnweather");

        var failure = Assert.Throws<CopilotStudioException>(() => DeclaredConnectionReferences.Declared(_workspacePath));

        Assert.Equal(CopilotErrorKinds.ConnectionReferencesConflict, failure.ErrorKind);
        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Contains("new_sharedteams_777c895a", failure.Message, StringComparison.Ordinal);
        Assert.Contains("shared_teams", failure.Message, StringComparison.Ordinal);
        Assert.Contains("shared_msnweather", failure.Message, StringComparison.Ordinal);
        Assert.Contains("connectionreferences.mcs.yml", failure.Message, StringComparison.Ordinal);
        Assert.Contains("new_sharedteams_777c895a.sync.yaml", failure.Message, StringComparison.Ordinal);
        Assert.NotNull(failure.Remediation);
    }

    [Fact]
    public void Declared_RefusesTwoCliFilesThatDisagreeAboutOneReference()
    {
        var folder = Path.Combine(_workspacePath, "infrastructure", "connections");
        Directory.CreateDirectory(folder);

        foreach (var (file, connector) in new[] { ("a", "shared_teams"), ("b", "shared_msnweather") })
        {
            File.WriteAllText(
                Path.Combine(folder, $"{file}.sync.yaml"),
                $"""
                connectionReferences:
                  - connectionReferenceLogicalName: cr_shared
                    connectorId: /providers/Microsoft.PowerApps/apis/{connector}
                """);
        }

        var failure = Assert.Throws<CopilotStudioException>(() => DeclaredConnectionReferences.Declared(_workspacePath));

        Assert.Equal(CopilotErrorKinds.ConnectionReferencesConflict, failure.ErrorKind);
        Assert.Contains("a.sync.yaml", failure.Message, StringComparison.Ordinal);
        Assert.Contains("b.sync.yaml", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Declared_RefusesOneFileThatDeclaresANameForTwoConnectors()
    {
        File.WriteAllText(
            Path.Combine(_workspacePath, "connectionreferences.mcs.yml"),
            """
            connectionReferences:
              - connectionReferenceLogicalName: cr_shared
                connectorId: /providers/Microsoft.PowerApps/apis/shared_teams
              - connectionReferenceLogicalName: cr_shared
                connectorId: /providers/Microsoft.PowerApps/apis/shared_msnweather
            """);

        var failure = Assert.Throws<CopilotStudioException>(() => DeclaredConnectionReferences.Declared(_workspacePath));

        Assert.Equal(CopilotErrorKinds.ConnectionReferencesConflict, failure.ErrorKind);
        Assert.Contains("shared_teams", failure.Message, StringComparison.Ordinal);
        Assert.Contains("shared_msnweather", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provision_DoesNotCreateAnythingWhenTwoDeclarationsDisagree()
    {
        WriteClassic("new_sharedteams_777c895a", "shared_teams");
        WriteCli("new_sharedteams_777c895a", "shared_msnweather");
        using var provider = SdkProvider();
        var client = new RecordingConnectionReferenceClient();

        await Assert.ThrowsAsync<CopilotStudioException>(() => ProvisionAsync(provider, client));

        Assert.Empty(client.EnsuredLogicalNames);
    }

    [Fact]
    public void Declared_IsEmptyWhenTheWorkspaceDeclaresNone()
    {
        Assert.Empty(DeclaredConnectionReferences.Declared(_workspacePath));
    }

    [Fact]
    public void Merge_AddsTheReferencesTheWorkflowUpsertDidNotReport()
    {
        var merged = DeclaredConnectionReferences.Merge(
            [Reference("new_sharedteams_777c895a")],
            [Reference("crf9a_nb1.cr.shared_sharepointonline.a972")]);

        Assert.Equal(
            ["crf9a_nb1.cr.shared_sharepointonline.a972", "new_sharedteams_777c895a"],
            Names(merged).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Merge_KeepsTheFirstRecordOfASharedName()
    {
        var preferred = new ConnectionReference.Builder
        {
            ConnectionReferenceLogicalName = new ConnectionReferenceLogicalName("new_sharedteams_777c895a"),
            ConnectionId = "preferred",
        }.Build();
        var additional = new ConnectionReference.Builder
        {
            ConnectionReferenceLogicalName = new ConnectionReferenceLogicalName("new_sharedteams_777c895a"),
            ConnectionId = "additional",
        }.Build();

        var merged = DeclaredConnectionReferences.Merge([preferred], [additional]);

        Assert.Equal("preferred", Assert.Single(merged).ConnectionId);
    }

    [Fact]
    public void Merge_WithDefaultArrays_DoesNotThrow()
    {
        Assert.Empty(DeclaredConnectionReferences.Merge(default, default));
        Assert.Equal(["new_sharedteams_777c895a"], Names(DeclaredConnectionReferences.Merge(default, [Reference("new_sharedteams_777c895a")])));
        Assert.Equal(["new_sharedteams_777c895a"], Names(DeclaredConnectionReferences.Merge([Reference("new_sharedteams_777c895a")], default)));
    }

    [Fact]
    public void WithProvisioned_AddsTheConfirmedReferencesAndKeepsTheReportedWorkflows()
    {
        var reported = new CloudFlowMetadata
        {
            Workflows = [new CloudFlowDefinition.Builder { DisplayName = "flow-1" }.Build()],
            ConnectionReferences = [Reference("new_sharedteams_777c895a")],
            Succeeded = false,
        };

        var seeded = DeclaredConnectionReferences.WithProvisioned(reported, [Reference("cli.cr.sharepoint")]);

        Assert.Equal(reported.Workflows, seeded.Workflows);
        Assert.False(seeded.Succeeded);
        Assert.Equal(
            ["cli.cr.sharepoint", "new_sharedteams_777c895a"],
            Names(seeded.ConnectionReferences).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void WithProvisioned_WithNothingConfirmed_LeavesTheReportedMetadataAlone()
    {
        var reported = new CloudFlowMetadata { ConnectionReferences = [Reference("new_sharedteams_777c895a")] };

        Assert.Same(reported, DeclaredConnectionReferences.WithProvisioned(reported, default));
        Assert.Null(DeclaredConnectionReferences.WithProvisioned(null, []));
    }

    [Fact]
    public void WithProvisioned_WithNoUpsertReport_StillCarriesTheConfirmedReferences()
    {
        var seeded = DeclaredConnectionReferences.WithProvisioned(null, [Reference("cli.cr.sharepoint")]);

        Assert.NotNull(seeded);
        Assert.Empty(seeded.Workflows);
        Assert.True(seeded.Succeeded);
        Assert.Equal(["cli.cr.sharepoint"], Names(seeded.ConnectionReferences));
    }

    [Fact]
    public void ABlankWorkspacePathIsRefused()
    {
        Assert.Throws<ArgumentException>(() => DeclaredConnectionReferences.Declared("  "));
    }

    private static ServiceProvider SdkProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITokenProvider>(new RecordingTokenProvider());
        services.AddAgentHostCopilotStudio(TextWriter.Null, PowerPlatformApiRingSelection.Default);
        return services.BuildServiceProvider();
    }

    private Task<ImmutableArray<ConnectionReference>> ProvisionAsync(
        ServiceProvider provider,
        ISyncDataverseClient client,
        IReadOnlyDictionary<string, Guid>? pushedConnectorIds = null,
        CancellationToken cancellationToken = default) =>
        DeclaredConnectionReferences.ProvisionAsync(
            _workspacePath,
            provider.GetRequiredService<IWorkspaceSynchronizer>(),
            client,
            pushedConnectorIds ?? new Dictionary<string, Guid>(),
            cancellationToken);

    [Fact]
    public async Task Provision_CachelessClassicCreatesItsDeclaredReferencesWithTheRealSdk()
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        using var provider = SdkProvider();
        var client = new RecordingConnectionReferenceClient();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            provider.GetRequiredService<IWorkspaceSynchronizer>()
                .ReadWorkspaceDefinitionAsync(new DirectoryPath(_workspacePath.Replace('\\', '/')), CancellationToken.None));

        var confirmed = await ProvisionAsync(provider, client);

        Assert.Equal(["classic.shared_teams"], client.EnsuredLogicalNames);
        var reference = Assert.Single(confirmed);
        Assert.Equal(client.References["classic.shared_teams"].ConnectionReferenceId, reference.Id.Value);
        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_teams", reference.ConnectorId.Value);
    }

    [Fact]
    public async Task Provision_CliConfirmsAllFiveReferencesIncludingThoseNotUsedByWorkflows()
    {
        WriteCli("new_approvals", "shared_advancedapprovals");
        WriteCli("new_weather", "shared_msnweather");
        WriteCli("new_teams", "shared_teams");
        WriteCli("cli.cr.sharepoint", "shared_sharepointonline");
        WriteCli("cli.cr.teams", "shared_teams");
        using var provider = SdkProvider();
        var client = new RecordingConnectionReferenceClient();

        var confirmed = await ProvisionAsync(provider, client);
        var repeated = await ProvisionAsync(provider, client);

        Assert.Equal(5, client.References.Count);
        Assert.Equal(5, confirmed.Length);
        Assert.Equal(confirmed.Select(reference => reference.Id.Value), repeated.Select(reference => reference.Id.Value));
        Assert.All(confirmed, reference => Assert.NotEqual(Guid.Empty, reference.Id.Value));
    }

    [Fact]
    public async Task Provision_PreservesTheExistingTargetsConnectionAndRowId()
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        using var provider = SdkProvider();
        var client = new RecordingConnectionReferenceClient();
        var id = Guid.NewGuid();
        client.References["classic.shared_teams"] = new SyncDataverseClient.ConnectionReferenceInfo
        {
            ConnectionReferenceId = id,
            ConnectionReferenceLogicalName = "classic.shared_teams",
            ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_teams",
            ConnectionId = "target-connection",
        };

        var confirmed = await ProvisionAsync(provider, client);

        var reference = Assert.Single(confirmed);
        Assert.Equal(id, reference.Id.Value);
        Assert.Equal("target-connection", reference.ConnectionId);
    }

    [Fact]
    public async Task Provision_ForwardsThePushedConnectorRowIdToTheSdk()
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        using var provider = SdkProvider();
        var client = new RecordingConnectionReferenceClient();
        var rowId = Guid.NewGuid();

        await ProvisionAsync(provider, client, new Dictionary<string, Guid> { ["shared_teams"] = rowId });

        Assert.Equal(rowId, Assert.Single(client.EnsuredCustomConnectorRowIds));
    }

    [Fact]
    public async Task Provision_TouchesNothingWhenTheWorkspaceDeclaresNoReferences()
    {
        using var provider = SdkProvider();
        var client = new RecordingConnectionReferenceClient { QueryThrows = new InvalidOperationException("must not be read") };

        Assert.Empty(await ProvisionAsync(provider, client));
        Assert.Empty(client.EnsuredLogicalNames);
    }

    [Fact]
    public async Task Provision_DoesNotRecreateAReferenceTheWorkspaceNoLongerDeclares()
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        using var provider = SdkProvider();
        var client = new RecordingConnectionReferenceClient();
        client.References["retired.shared_msnweather"] = new SyncDataverseClient.ConnectionReferenceInfo
        {
            ConnectionReferenceId = Guid.NewGuid(),
            ConnectionReferenceLogicalName = "retired.shared_msnweather",
            ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_msnweather",
        };

        var confirmed = await ProvisionAsync(provider, client);

        Assert.Equal(["classic.shared_teams"], client.EnsuredLogicalNames);
        Assert.Equal(["classic.shared_teams"], Names(confirmed));
    }

    [Fact]
    public async Task Provision_RequestsTheConnectorTheWorkspaceDeclaresRatherThanTheOneTheTargetHolds()
    {
        WriteClassic("classic.connector", "shared_teams");
        using var provider = SdkProvider();
        var client = new RecordingConnectionReferenceClient();

        await ProvisionAsync(provider, client);

        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_teams", Assert.Single(client.EnsuredConnectorIds));
    }

    [Fact]
    public async Task Provision_RejectsARowStillPointingAtTheConnectorTheDeclarationReplaced()
    {
        WriteClassic("classic.connector", "shared_teams");
        using var provider = SdkProvider();
        var client = new RecordingConnectionReferenceClient();
        client.References["classic.connector"] = new SyncDataverseClient.ConnectionReferenceInfo
        {
            ConnectionReferenceId = Guid.NewGuid(),
            ConnectionReferenceLogicalName = "classic.connector",
            ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_msnweather",
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => ProvisionAsync(provider, client));

        Assert.Equal(CopilotErrorKinds.ServiceError, failure.ErrorKind);
        Assert.Contains("classic.connector", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provision_AcceptsACustomConnectorTheTargetReidentifiedForItsOwnEnvironment()
    {
        WriteClassic("classic.custom", "shared_contoso-5f0123456789abcdef");
        using var provider = SdkProvider();
        var client = new RecordingConnectionReferenceClient();
        client.References["classic.custom"] = new SyncDataverseClient.ConnectionReferenceInfo
        {
            ConnectionReferenceId = Guid.NewGuid(),
            ConnectionReferenceLogicalName = "classic.custom",
            ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_contoso-5ffedcba9876543210",
        };

        Assert.Equal(["classic.custom"], Names(await ProvisionAsync(provider, client)));
    }

    [Fact]
    public async Task Provision_AcceptsACustomConnectorWhoseDeclarationSpellsItsSuffixSeparatorInUpperCase()
    {
        WriteClassic("classic.custom", "shared_contoso-5F0123456789abcdef");
        using var provider = SdkProvider();
        var client = new RecordingConnectionReferenceClient();
        client.References["classic.custom"] = new SyncDataverseClient.ConnectionReferenceInfo
        {
            ConnectionReferenceId = Guid.NewGuid(),
            ConnectionReferenceLogicalName = "classic.custom",
            ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_contoso-5ffedcba9876543210",
        };

        Assert.Equal(["classic.custom"], Names(await ProvisionAsync(provider, client)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provision_RejectsUnconfirmedReferencesEvenWhenTheSdkReturnsSuccess(bool failed)
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        using var provider = SdkProvider();
        var client = new RecordingConnectionReferenceClient
        {
            ProvisioningThrows = failed ? new InvalidOperationException("denied") : null,
            OmitProvisionedReferences = !failed,
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => ProvisionAsync(provider, client));

        Assert.Equal(CopilotErrorKinds.ServiceError, failure.ErrorKind);
        Assert.Contains("classic.shared_teams", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provision_DoesNotConvertAReadFailureToAnEmptyBaseline()
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        using var provider = SdkProvider();
        var expected = new HttpRequestException("lookup failed");
        var client = new RecordingConnectionReferenceClient { QueryThrows = expected };

        Assert.Same(expected, await Assert.ThrowsAsync<HttpRequestException>(() => ProvisionAsync(provider, client)));
    }

    [Fact]
    public async Task Provision_HonorsCancellationBeforeCreatingReferences()
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        using var provider = SdkProvider();
        var client = new RecordingConnectionReferenceClient();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProvisionAsync(provider, client, cancellationToken: cancellation.Token));

        Assert.Empty(client.EnsuredLogicalNames);
    }
}
