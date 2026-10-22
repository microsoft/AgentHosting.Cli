using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.Tests;

internal static class AbsContractEnvelope
{
    internal static JsonElement WithoutNumericErrorCode(JsonElement envelope, string fixtureFile)
    {
        if (envelope.GetProperty("success").GetBoolean())
        {
            Assert.False(envelope.TryGetProperty("errorCode", out _));
            return envelope;
        }

        var expectedCode = fixtureFile switch
        {
            "06-existing-bot-different-app-id.json" => 16004,
            "07-missing-required-context.json" => 16002,
            "08-provider-not-registered.json" => 16005,
            "09-target-tenant-auth-failure.json" => 12007,
            "10-secret-shaped-output-rejection.json" => 16010,
            _ => throw new InvalidOperationException($"No numeric error code is specified for '{fixtureFile}'."),
        };

        Assert.Equal(expectedCode, envelope.GetProperty("errorCode").GetInt32());

        return JsonSerializer.SerializeToElement(envelope.EnumerateObject()
            .Where(property => property.Name != "errorCode")
            .ToDictionary(property => property.Name, property => property.Value));
    }
}
