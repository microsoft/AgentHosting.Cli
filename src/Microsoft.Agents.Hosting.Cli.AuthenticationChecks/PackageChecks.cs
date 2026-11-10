using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace Microsoft.Agents.Hosting.Cli.AuthenticationChecks;

public static class PackageChecks
{
    public static IReadOnlyList<string> RuntimeIdentifiers { get; } =
        ["osx-x64", "osx-arm64", "win-x86", "win-x64", "win-arm64", "linux-x64", "linux-arm64"];

    public const string PackageId = "Microsoft.Agents.Hosting.Cli";

    public static void CheckImplementation(ZipArchive package, string rid)
    {
        if (!RuntimeIdentifiers.Contains(rid, StringComparer.Ordinal)) throw new InvalidOperationException("Unknown tool RID.");
        var prefix = $"tools/net10.0/{rid}/";
        RequireEntry(package, prefix + PackageId + (rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : ""));
        if (BrokerLibrary(rid) is { } library) RequireEntry(package, prefix + library);
        var shims = RuntimeIdentifiers.Select(shim =>
            prefix + $"shims/{shim}/msagent{(shim.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "")}").ToArray();
        if (!package.Entries.Where(e => e.FullName.StartsWith(prefix + "shims/", StringComparison.Ordinal) &&
            !e.FullName.EndsWith('/')).Select(e => e.FullName)
            .ToHashSet(StringComparer.Ordinal).SetEquals(shims))
            throw new InvalidOperationException("Tool must retain exactly the seven declared shim RIDs.");
        foreach (var shim in shims) RequireEntry(package, shim);
        foreach (var assembly in new[] { PackageId, PackageId + ".Auth", PackageId + ".Core", PackageId + ".Commands", PackageId + ".CopilotStudio" })
            RequireEntry(package, prefix + assembly + ".dll");
        using var runtime = ReadJson(package, prefix + PackageId + ".runtimeconfig.json");
        var options = runtime.RootElement.GetProperty("runtimeOptions");
        if (!options.TryGetProperty("frameworks", out var frameworks) || options.TryGetProperty("includedFrameworks", out _) ||
            options.GetProperty("tfm").GetString() != "net10.0" ||
            !frameworks.EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["Microsoft.NETCore.App", "Microsoft.AspNetCore.App"]) ||
            frameworks.EnumerateArray().Any(f => f.GetProperty("version").GetString() != "10.0.0"))
            throw new InvalidOperationException("Tool must retain both .NET 10 shared frameworks.");
        using var flags = ReadJson(package, prefix + "featureflags.json");
        var expected = new Dictionary<string, string>
        {
            ["amsFeatures"] = "off", ["azureBotServiceFeatures"] = "off", ["identityFeatures"] = "off",
            ["liveTestFeatures"] = "off", ["evaluationFeatures"] = "on", ["legacyAuth"] = "off",
        };
        if (flags.RootElement.EnumerateObject().Count() != expected.Count ||
            expected.Any(f => !flags.RootElement.TryGetProperty(f.Key, out var value) || value.GetString() != f.Value))
            throw new InvalidOperationException("Packaged feature defaults changed.");
        var command = ReadXml(package, prefix + "DotnetToolSettings.xml").Descendants("Command").Single();
        if (command.Attribute("Name")?.Value != "msagent" || command.Attribute("Runner")?.Value != "executable" ||
            command.Attribute("EntryPoint")?.Value != PackageId + (rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : ""))
            throw new InvalidOperationException("Installed tool entry point changed.");
        using var deps = ReadJson(package, prefix + PackageId + ".deps.json");
        foreach (var dependency in new[] { "Microsoft.Identity.Client/4.92.0", "Microsoft.Identity.Client.Broker/4.92.0",
            "Microsoft.Identity.Client.Extensions.Msal/4.92.0", "Azure.Identity/1.21.0", "Azure.Core/1.53.0",
            "Microsoft.Agents.CopilotStudio.Sync/1.10.9-prerelease" })
            if (!deps.RootElement.GetProperty("libraries").TryGetProperty(dependency, out _))
                throw new InvalidOperationException("Packaged authentication dependency contract changed.");
    }

    public static (string Version, IReadOnlyList<string> Packages) CheckFamily(string packageDirectory)
    {
        var primary = Directory.GetFiles(packageDirectory, PackageId + ".*.nupkg", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p).StartsWith(PackageId + ".", StringComparison.Ordinal) &&
                !RuntimeIdentifiers.Any(r => Path.GetFileName(p).StartsWith(PackageId + "." + r + ".", StringComparison.Ordinal)))
            .ToArray();
        if (primary.Length != 1) throw new InvalidOperationException("Exactly one freshly packed tool family is required.");
        using var parent = ZipFile.OpenRead(primary[0]);
        var version = CheckIdentity(parent, PackageId, "DotnetTool");
        var mapping = ReadXml(parent, "tools/any/any/DotnetToolSettings.xml");
        if (mapping.Descendants("Command").Single().Attribute("Name")?.Value != "msagent")
            throw new InvalidOperationException("Primary tool command changed.");
        var rids = mapping.Descendants("RuntimeIdentifierPackage").ToArray();
        if (rids.Length != RuntimeIdentifiers.Count ||
            !rids.Select(r => r.Attribute("RuntimeIdentifier")?.Value).ToHashSet(StringComparer.Ordinal).SetEquals(RuntimeIdentifiers) ||
            rids.Any(r => r.Attribute("Id")?.Value != PackageId + "." + r.Attribute("RuntimeIdentifier")?.Value))
            throw new InvalidOperationException("Primary package must route all seven exact RIDs.");
        var packages = new List<string> { primary[0] };
        foreach (var rid in RuntimeIdentifiers)
        {
            var id = PackageId + "." + rid;
            var paths = Directory.GetFiles(packageDirectory, id + "." + version + ".nupkg", SearchOption.AllDirectories);
            if (paths.Length != 1) throw new InvalidOperationException("A matching RID package is missing or ambiguous.");
            using var implementation = ZipFile.OpenRead(paths[0]);
            if (CheckIdentity(implementation, id, "DotnetToolRidPackage") != version)
                throw new InvalidOperationException("RID package versions differ.");
            CheckImplementation(implementation, rid);
            packages.Add(paths[0]);
        }
        return (version, packages);
    }

    public static string? BrokerLibrary(string rid) => rid switch
    {
        "win-x86" => "msalruntime_x86.dll",
        "win-x64" => "msalruntime.dll",
        "win-arm64" => "msalruntime_arm64.dll",
        "osx-x64" => "msalruntime.dylib",
        "osx-arm64" => "msalruntime_arm64.dylib",
        "linux-x64" => "libmsalruntime.so",
        _ => null,
    };

    private static string CheckIdentity(ZipArchive archive, string id, string packageType)
    {
        var nuspec = ReadXml(archive, id + ".nuspec");
        var metadata = nuspec.Descendants().Single(e => e.Name.LocalName == "metadata");
        if (metadata.Elements().Single(e => e.Name.LocalName == "id").Value != id ||
            metadata.Descendants().Single(e => e.Name.LocalName == "packageType").Attribute("name")?.Value != packageType)
            throw new InvalidOperationException("Tool package identity/type changed.");
        return metadata.Elements().Single(e => e.Name.LocalName == "version").Value;
    }

    private static ZipArchiveEntry RequireEntry(ZipArchive archive, string path) =>
        archive.GetEntry(path) is { Length: > 0 } entry ? entry : throw new InvalidOperationException("Required tool asset missing.");

    private static JsonDocument ReadJson(ZipArchive archive, string path)
    {
        using var stream = RequireEntry(archive, path).Open();
        return JsonDocument.Parse(stream);
    }

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        using var stream = RequireEntry(archive, path).Open();
        return XDocument.Load(stream);
    }
}
