using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class BotEntitySettingsProjectionTests
{
    private static readonly DateTime FirstPublish = new(2026, 10, 8, 11, 49, 46, DateTimeKind.Utc);
    private static readonly DateTime SecondPublish = new(2026, 10, 8, 12, 20, 32, DateTimeKind.Utc);

    private static BotEntity Agent(string displayName = "Repro Bot", DateTime? publishedOn = null, long version = 1) =>
        new BotEntity.Builder
        {
            SchemaName = new BotEntitySchemaName("cr_reprobot"),
            CdsBotId = Guid.Parse("d00f80ef-07c3-f111-aaaf-000d3a367518"),
            DisplayName = displayName,
            Version = version,
            PublishedOn = publishedOn,
        }.Build();

    [Fact]
    public void AnAgentThatWasPublishedMatchesTheSameAgentBeforeItWasPublished()
    {
        var published = Agent(publishedOn: FirstPublish).WithOnlyAuthoredSettingsProperties();
        var unpublished = Agent(publishedOn: null).WithOnlyAuthoredSettingsProperties();

        Assert.True(published.Equals(unpublished, NodeComparison.Structural));
    }

    [Fact]
    public void RepublishingTheSameContentLeavesTheAuthoredSettingsUnchanged()
    {
        var first = Agent(publishedOn: FirstPublish).WithOnlyAuthoredSettingsProperties();
        var second = Agent(publishedOn: SecondPublish).WithOnlyAuthoredSettingsProperties();

        Assert.True(first.Equals(second, NodeComparison.Structural));
    }

    [Fact]
    public void TheProjectionCarriesNoPublishReceipt()
    {
        Assert.Null(Agent(publishedOn: FirstPublish).WithOnlyAuthoredSettingsProperties().PublishedOn);
    }

    [Fact]
    public void AnAuthoredEditIsStillVisibleAcrossAPublish()
    {
        var published = Agent(displayName: "Repro Bot", publishedOn: FirstPublish).WithOnlyAuthoredSettingsProperties();
        var edited = Agent(displayName: "Renamed Bot", publishedOn: null).WithOnlyAuthoredSettingsProperties();

        Assert.False(published.Equals(edited, NodeComparison.Structural));
    }

    [Fact]
    public void TheServicePublishReceiptIsRestoredOntoARebuiltEntity()
    {
        var reported = Agent(publishedOn: SecondPublish);

        var restored = Agent(publishedOn: null).WithOnlyAuthoredSettingsProperties().WithPublishStateFrom(reported);

        Assert.Equal(SecondPublish, restored.PublishedOn);
    }

    [Fact]
    public void AnAgentTheServiceNeverPublishedIsRestoredWithoutAReceipt()
    {
        var restored = Agent(publishedOn: FirstPublish).WithPublishStateFrom(Agent(publishedOn: null));

        Assert.Null(restored.PublishedOn);
    }
}
