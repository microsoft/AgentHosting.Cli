using System.Reflection;
using Microsoft.Agents.Hosting.Cli.Core.Abs;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Offline unit tests for the read-only ABS preview core, exercised through the public
/// <see cref="AbsProvisioningService.PreviewAsync"/> so the desired-state validation, zero-Graph
/// planner (create/update/no-op/conflict), and secret-material guard are covered end-to-end with
/// fakes. Complements the fixture-parity runner with the scenarios the preview subset of fixtures does
/// not cover (update, no-op, both conflict shapes, and each validation branch).
/// </summary>
public sealed class AbsProvisioningServiceTests
{
    private const string BotResourceId =
        "/subscriptions/sub-contoso/resourceGroups/rg-agenthost/providers/Microsoft.BotService/botServices/agenthost-bot";

    private const string SuppliedAppId = "11111111-1111-1111-1111-111111111111";

    private static AbsProvisioningService Service(FakeAbsStateReader? reader = null) =>
        new(reader ?? new FakeAbsStateReader());

    private static AbsProvisioningRequest Request(
        string? endpoint = "https://gateway.contoso.com/api/messages",
        string? msaAppId = SuppliedAppId,
        string? sku = null,
        string? tenant = "tenant-contoso",
        string? identityMode = null) => new()
    {
        Tenant = tenant,
        Subscription = "sub-contoso",
        ResourceGroup = "rg-agenthost",
        Location = "westus",
        BotName = "agenthost-bot",
        Endpoint = endpoint,
        MsaAppId = msaAppId,
        Sku = sku,
        IdentityMode = identityMode,
    };

    private static AbsPlannedChange BotChange(AbsProvisioningReport report) =>
        report.Changes.Single(c => c.Resource == "botService");

    // ── planner: create / update / no-op ──────────────────────────────────────────────

    [Fact]
    public async Task Preview_Greenfield_PlansBotServiceCreate_WithSuppliedAppNoops()
    {
        var report = await Service().PreviewAsync(Request(), default);

        Assert.Equal("planned", report.Status);
        Assert.Equal(SuppliedAppId, report.Abs.AppId);
        Assert.Equal("SingleTenant", report.Abs.MsaAppType);
        Assert.Equal("create", BotChange(report).Action);
        Assert.All(
            report.Changes.Where(c => c.Resource != "botService"),
            c =>
            {
                Assert.Equal("noop", c.Action);
                Assert.Equal(SuppliedAppId, c.Target);
            });
        Assert.Contains(report.Preflight, p => p.Id == "existingAppConflict" && p.Status == "pass");
        var writeCapability = Assert.Single(report.Preflight, p => p.Id == "writeCapability");
        Assert.Contains("ARM write capability", writeCapability.Detail);
        Assert.DoesNotContain("Graph", writeCapability.Detail);
        var audienceReachability = Assert.Single(report.Preflight, p => p.Id == "audienceReachability");
        Assert.Contains("ARM read calls succeeded", audienceReachability.Detail);
        Assert.Contains("requires no Microsoft Graph call", audienceReachability.Detail);
    }

    [Fact]
    public async Task Preview_ExistingBotWithDifferentEndpoint_PlansUpdate()
    {
        var reader = new FakeAbsStateReader
        {
            ExistingBot = new AbsBotSnapshot
            {
                BotResourceId = BotResourceId,
                MessagingEndpoint = "https://old.contoso.com/api/messages",
                Sku = "F0",
            },
        };

        var report = await Service(reader).PreviewAsync(Request(), default);

        var bot = BotChange(report);
        Assert.Equal("update", bot.Action);
        Assert.Contains(
            "endpoint https://old.contoso.com/api/messages → https://gateway.contoso.com/api/messages",
            bot.Detail);
    }

    [Fact]
    public async Task Preview_ExistingBotMatchesDesired_PlansNoop()
    {
        var reader = new FakeAbsStateReader
        {
            ExistingBot = new AbsBotSnapshot
            {
                BotResourceId = BotResourceId,
                MessagingEndpoint = "https://gateway.contoso.com/api/messages",
                Sku = "F0",
            },
        };

        var report = await Service(reader).PreviewAsync(Request(), default);

        Assert.Equal("noop", BotChange(report).Action);
        Assert.Contains(
            report.NextActions,
            a => a.Contains("No changes are required", StringComparison.Ordinal));
    }

    // ── planner: conflicts (preview reports, never throws) ─────────────────────────────

    [Fact]
    public async Task Preview_ExistingBotBoundToDifferentAppId_ReportsConflict_WithoutThrowing()
    {
        var reader = new FakeAbsStateReader
        {
            ExistingBot = new AbsBotSnapshot
            {
                BotResourceId = BotResourceId,
                AppId = "99999999-9999-9999-9999-999999999999",
                MessagingEndpoint = "https://gateway.contoso.com/api/messages",
                Sku = "F0",
            },
        };

        var report = await Service(reader).PreviewAsync(Request(), default);

        Assert.Equal("planned", report.Status);
        Assert.Equal("conflict", BotChange(report).Action);
        Assert.Contains(report.Preflight, p => p.Id == "existingBotConflict" && p.Status == "fail");
        Assert.Contains(
            report.NextActions,
            a => a.Contains("Resolve the reported conflict(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_ExistingBotWithDifferentMsaAppType_ReportsConflict()
    {
        var reader = new FakeAbsStateReader
        {
            ExistingBot = new AbsBotSnapshot { BotResourceId = BotResourceId, MsaAppType = "MultiTenant" },
        };

        var report = await Service(reader).PreviewAsync(Request(), default);

        var bot = BotChange(report);
        Assert.Equal("conflict", bot.Action);
        Assert.Contains("msaAppType", bot.Detail);
    }

    // ── validation (UsageException / missing-context, exit 2) ──────────────────────────

    [Fact]
    public async Task Preview_NonHttpsEndpoint_ThrowsUsage_Exit2()
    {
        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            Service().PreviewAsync(Request(endpoint: "http://gateway.contoso.com/api/messages"), default));

        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Null(ex.ErrorKind);
    }

    [Fact]
    public async Task Preview_UnsupportedSku_ThrowsUsage()
    {
        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            Service().PreviewAsync(Request(sku: "P1"), default));

        Assert.Contains("Unsupported SKU", ex.Message);
    }

    [Fact]
    public async Task Preview_MissingTenant_ThrowsMissingContext_Exit2()
    {
        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Service().PreviewAsync(Request(tenant: null), default));

        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Equal("missing-context", ex.ErrorKind);
        Assert.Contains("--tenant", ex.Message);
    }

    [Fact]
    public async Task Preview_MissingMsaAppId_ThrowsMissingContext_ListingTheFlag()
    {
        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Service().PreviewAsync(Request(msaAppId: null), default));

        Assert.Equal("missing-context", ex.ErrorKind);
        Assert.Contains("--msa-app-id", ex.Message);
    }

    // ── secret-material guard (exit 1, no kind/remediation) ────────────────────────────

    [Fact]
    public async Task Preview_SecretShapedAppId_ThrowsSecretMaterial_Exit1_PathsOnly()
    {
        var ex = await Assert.ThrowsAsync<SecretMaterialException>(() =>
            Service().PreviewAsync(Request(msaAppId: "eyJhbGciOiJ.eyJzdWIiOiJ.SflKxwRJ"), default));

        Assert.Equal(ExitCodes.Failure, ex.ExitCode);
        Assert.Null(ex.ErrorKind);
        Assert.Null(ex.Remediation);
        Assert.Contains("abs.appId", ex.Message);
        Assert.DoesNotContain("eyJ", ex.Message); // paths only — never the value.
    }

    // ── Structural read-only guarantee ────────────────────────────────────────────────

    [Fact]
    public void PreviewService_HasNoReferenceToMutationPort()
    {
        var ctorParameterTypes = typeof(AbsProvisioningService)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);
        Assert.DoesNotContain(typeof(IAbsMutationPort), ctorParameterTypes);

        var fieldTypes = typeof(AbsProvisioningService)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(f => f.FieldType);
        Assert.DoesNotContain(typeof(IAbsMutationPort), fieldTypes);
    }
}
