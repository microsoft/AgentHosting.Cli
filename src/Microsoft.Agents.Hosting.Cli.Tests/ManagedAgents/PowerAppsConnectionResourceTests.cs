using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class PowerAppsConnectionResourceTests
{
    private static JsonElement Element(string json, out JsonDocument document)
    {
        document = JsonDocument.Parse(json);

        return document.RootElement;
    }

    [Fact]
    public void Parse_ReadsAnEmptyBodyAsAnEmptyObject()
    {
        using var document = PowerAppsConnectionResource.Parse(string.Empty);

        Assert.NotNull(document);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void Parse_ReturnsNothingForContentThatIsNotJson() =>
        Assert.Null(PowerAppsConnectionResource.Parse("not json"));

    [Fact]
    public void DisplayName_IsReadFromProperties()
    {
        var element = Element(ConnectionFixtures.SsoConnector, out var document);

        using (document)
        {
            Assert.Equal("Office 365 Outlook", PowerAppsConnectionResource.DisplayNameOf(element));
        }
    }

    [Fact]
    public void DisplayName_IsAbsentWhenThereAreNoProperties()
    {
        var element = Element("{}", out var document);

        using (document)
        {
            Assert.Null(PowerAppsConnectionResource.DisplayNameOf(element));
        }
    }

    [Fact]
    public void ConsentLinks_AreReadFromTheConsentResponse()
    {
        var element = Element(ConnectionFixtures.ConsentResponse(), out var document);

        using (document)
        {
            Assert.Equal(new Uri(ConnectionFixtures.ConsentLink), PowerAppsConnectionResource.ConsentLinkOf(element));
        }
    }

    [Fact]
    public void ConsentLinks_AreAbsentWhenTheServiceOffersNone()
    {
        var element = Element(ConnectionFixtures.ConsentResponse(withConsentLink: false), out var document);

        using (document)
        {
            Assert.Null(PowerAppsConnectionResource.ConsentLinkOf(element));
        }
    }

    [Fact]
    public void ConsentLinks_AreAbsentWhenTheValueIsNotAnAbsoluteUrl()
    {
        var element = Element("{\"consentLink\":\"not a url\"}", out var document);

        using (document)
        {
            Assert.Null(PowerAppsConnectionResource.ConsentLinkOf(element));
        }
    }

    [Fact]
    public void Unauthenticated_IsReportedFromTheFirstStatusError()
    {
        var element = Element(ConnectionFixtures.Connection(authenticated: false), out var document);

        using (document)
        {
            Assert.True(PowerAppsConnectionResource.ReportsUnauthenticated(element));
        }
    }

    [Fact]
    public void Unauthenticated_IsNotReportedWhenThereAreNoStatuses()
    {
        var element = Element(ConnectionFixtures.Connection(authenticated: true), out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsUnauthenticated(element));
        }
    }

    [Theory]
    [InlineData("Unauthenticated")]
    [InlineData("unauthenticated")]
    [InlineData("Unauthorized")]
    public void Unauthenticated_MatchesEitherCodeCaseInsensitively(string code)
    {
        var element = Element("{\"properties\":{\"statuses\":[{\"status\":\"Error\",\"error\":{\"code\":\"" + code + "\"}}]}}", out var document);

        using (document)
        {
            Assert.True(PowerAppsConnectionResource.ReportsUnauthenticated(element));
        }
    }

    [Fact]
    public void Unauthenticated_IsNotReportedForAnUnrelatedErrorCode()
    {
        var element = Element("{\"properties\":{\"statuses\":[{\"status\":\"Error\",\"error\":{\"code\":\"Throttled\"}}]}}", out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsUnauthenticated(element));
        }
    }

    [Fact]
    public void Unauthenticated_IsNotReportedWhenTheFirstStatusCarriesNoError()
    {
        var element = Element("{\"properties\":{\"statuses\":[{\"status\":\"Connected\"}]}}", out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsUnauthenticated(element));
        }
    }

    [Fact]
    public void Unauthenticated_IsNotReportedForABodyThatCarriesNoProperties()
    {
        var element = Element("{}", out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsUnauthenticated(element));
        }
    }

    [Fact]
    public void Ready_IsNotReportedWhenTheServiceListsNoStatuses()
    {
        var element = Element("{\"name\":\"conn-1\",\"properties\":{\"displayName\":\"Office 365 Outlook\"}}", out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsReady(element));
        }
    }

    [Fact]
    public void Ready_IsReportedWhenTheFirstStatusIsConnected()
    {
        var element = Element(ConnectionFixtures.ReadyConnection(), out var document);

        using (document)
        {
            Assert.True(PowerAppsConnectionResource.ReportsReady(element));
        }
    }

    [Fact]
    public void Ready_IsNotReportedWhenTheStatusesArrayIsEmpty()
    {
        var element = Element("{\"properties\":{\"statuses\":[]}}", out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsReady(element));
        }
    }

    [Fact]
    public void Ready_IsNotReportedWhenALaterStatusCarriesAnError()
    {
        var element = Element("{\"properties\":{\"statuses\":[{\"status\":\"Connected\"},{\"status\":\"Error\",\"error\":{\"code\":\"Unauthenticated\"}}]}}", out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsReady(element));
        }
    }

    [Fact]
    public void Ready_IsNotReportedWhenALaterStatusIsNotConnected()
    {
        var element = Element("{\"properties\":{\"statuses\":[{\"status\":\"Connected\"},{\"status\":\"Disconnected\"}]}}", out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsReady(element));
        }
    }

    [Theory]
    [InlineData("Disconnected")]
    [InlineData("Pending")]
    [InlineData("")]
    public void Ready_IsNotReportedForAStatusThatIsNotConnected(string status)
    {
        var element = Element(ConnectionFixtures.ConnectionWithStatus("{\"status\":\"" + status + "\"}"), out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsReady(element));
        }
    }

    [Theory]
    [InlineData("{\"properties\":{\"statuses\":{}}}")]
    [InlineData("{\"properties\":{\"statuses\":\"Connected\"}}")]
    [InlineData("{\"properties\":{\"statuses\":[\"Connected\"]}}")]
    [InlineData("{\"properties\":{\"statuses\":[42]}}")]
    [InlineData("{\"properties\":{\"statuses\":[null]}}")]
    public void Ready_IsNotReportedForMalformedStatuses(string body)
    {
        var element = Element(body, out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsReady(element));
        }
    }

    [Fact]
    public void Ready_IsNotReportedForABodyThatDescribesNoConnection()
    {
        var element = Element("{}", out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsReady(element));
        }
    }

    [Fact]
    public void Ready_IsNotReportedForAnEmptyBody()
    {
        using var document = PowerAppsConnectionResource.Parse(string.Empty);

        Assert.False(PowerAppsConnectionResource.ReportsReady(document!.RootElement));
    }

    [Fact]
    public void Ready_IsNotReportedWhenTheServiceNamedTheConnectionButDescribedNothingAboutIt()
    {
        var element = Element("{\"name\":\"conn-1\"}", out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsReady(element));
        }
    }

    [Fact]
    public void Ready_IsNotReportedWhenTheConnectionStillNeedsSigningIn()
    {
        var element = Element(ConnectionFixtures.Connection(authenticated: false), out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsReady(element));
        }
    }

    [Theory]
    [InlineData("Throttled")]
    [InlineData("ApiConnectionLimitExceeded")]
    public void Ready_IsNotReportedForAnyErrorCodeNotJustAuthentication(string code)
    {
        var element = Element(ConnectionFixtures.ConnectionWithStatus("{\"status\":\"Error\",\"error\":{\"code\":\"" + code + "\"}}"), out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsReady(element));
            Assert.False(PowerAppsConnectionResource.ReportsUnauthenticated(element));
        }
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("error")]
    public void Ready_IsNotReportedWhenTheFirstStatusIsAnErrorCarryingNoDetail(string status)
    {
        var element = Element(ConnectionFixtures.ConnectionWithStatus("{\"status\":\"" + status + "\"}"), out var document);

        using (document)
        {
            Assert.False(PowerAppsConnectionResource.ReportsReady(element));
        }
    }

    [Theory]
    [InlineData("{\"status\":\"Error\",\"error\":{\"code\":\"Throttled\",\"message\":\"too many requests\"}}", "Throttled")]
    [InlineData("{\"status\":\"Error\",\"error\":{\"code\":\"Throttled\"}}", "Throttled")]
    [InlineData("{\"status\":\"Error\",\"error\":{\"message\":\"too many requests\"}}", "Error")]
    [InlineData("{\"status\":\"Error\",\"error\":{}}", "Error")]
    [InlineData("{\"status\":\"Error\"}", "Error")]
    public void ErrorCode_NamesOnlyTheCodeTheServiceReported(string statusJson, string expected)
    {
        var element = Element(ConnectionFixtures.ConnectionWithStatus(statusJson), out var document);

        using (document)
        {
            Assert.Equal(expected, PowerAppsConnectionResource.ErrorCodeOf(element));
        }
    }

    [Fact]
    public void ErrorCode_DoesNotCarryTheServicesOwnMessage()
    {
        const string statusJson = "{\"status\":\"Error\",\"error\":{\"code\":\"Throttled\",\"message\":\"contoso-sql-01 refused: password expired\"}}";

        var element = Element(ConnectionFixtures.ConnectionWithStatus(statusJson), out var document);

        using (document)
        {
            Assert.DoesNotContain("contoso-sql-01", PowerAppsConnectionResource.ErrorCodeOf(element), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("{\"status\":\"Connected\"}")]
    [InlineData("{\"status\":\"Connected\",\"target\":\"Token\"}")]
    public void ErrorCode_IsAbsentForAConnectionReportingNoFailure(string statusJson)
    {
        var element = Element(ConnectionFixtures.ConnectionWithStatus(statusJson), out var document);

        using (document)
        {
            Assert.Null(PowerAppsConnectionResource.ErrorCodeOf(element));
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"properties\":{\"statuses\":[]}}")]
    public void ErrorCode_IsAbsentWhenTheServiceReportedNoStatus(string body)
    {
        var element = Element(body, out var document);

        using (document)
        {
            Assert.Null(PowerAppsConnectionResource.ErrorCodeOf(element));
        }
    }
}
