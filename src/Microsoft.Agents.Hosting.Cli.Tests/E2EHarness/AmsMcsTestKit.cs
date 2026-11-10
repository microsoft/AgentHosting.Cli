using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

[CollectionDefinition(AmsMcsTestKit.Collection, DisableParallelization = true)]
public sealed class AmsMcsEnvironmentCollection
{
}

internal static class AmsMcsTestKit
{
    public const string Collection = "AmsMcsEnvironment";

    public const string Dev = "aaaaaaaa-0000-4000-8000-000000000001";

    public const string Test = "bbbbbbbb-0000-4000-8000-000000000002";

    public const string Prod = "cccccccc-0000-4000-8000-000000000003";

    public const string Elsewhere = "dddddddd-0000-4000-8000-000000000004";

    public const string Tenant = "eeeeeeee-0000-4000-8000-000000000005";

    public const string GitHost = "git.example.invalid";

    public static AmsMcsConfig Config(string? workspaceRoot = null) => new()
    {
        Features = "AmsMcsAgents",
        DataPlane = new AmsServiceConfig { Endpoint = "https://data-plane.example.invalid", Audience = "https://data-plane-audience.example.invalid" },
        OnBehalfOf = new AmsServiceConfig { Endpoint = "https://on-behalf-of.example.invalid", Audience = "https://on-behalf-of-audience.example.invalid" },
        ControlPlane = new AmsServiceConfig { Endpoint = "https://control-plane.example.invalid", Audience = "https://control-plane-audience.example.invalid" },
        GitHost = GitHost,
        DevEnvironmentId = Dev,
        TestEnvironmentId = Test,
        ProdEnvironmentId = Prod,
        Organizations = new AmsMcsOrganizations { Dev = "https://dev.example.invalid", Test = "https://test.example.invalid", Prod = "https://prod.example.invalid" },
        WorkspaceRoot = workspaceRoot,
    };

    public static string EnvironmentList(string? active, params string[] listed) => $$"""{"status":"environments-listed","environments":[{{string.Join(',', listed.Select(id => $$"""{"environmentId":"{{id}}","displayName":"Environment {{id[..4]}}","tenantId":"{{Tenant}}","apiEndpoint":"https://api.example.invalid","isActive":{{(id == active ? "true" : "false")}}}"""))}}]}""";

    public static string AgentList(params string[] agentIds) => $$"""{"scope":"environment","view":"ams","agents":[{{string.Join(',', agentIds.Select(id => $$"""{"agentId":"{{id}}","tenantId":"{{Tenant}}","environmentId":"{{Dev}}","displayName":"Agent {{id[..4]}}","agentType":"MCSAgent"}"""))}}]}""";

    public static string Operation(string status, string? errorCode = null) => $$"""{"operationId":"op-1","kind":"deploy","status":"{{status}}","agentId":"{{Dev}}","errorCode":{{(errorCode is null ? "null" : $"\"{errorCode}\"")}},"errorMessage":"the deploy stopped"}""";

    public static string Script(string directory, string name, string windowsBody, string unixBody)
    {
        if (OperatingSystem.IsWindows())
        {
            var batch = Path.Combine(directory, name + ".cmd");
            File.WriteAllText(batch, "@echo off\r\n" + windowsBody);
            return batch;
        }

        var script = Path.Combine(directory, name);
        File.WriteAllText(script, "#!/bin/sh\n" + unixBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    public static string MsagentScript(string directory, string token, string agentListJson) => Script(
        directory,
        "msagent",
        $"if \"%1\"==\"auth\" (\r\necho username=msagent\r\necho password={token}\r\nexit /b 0\r\n)\r\necho {agentListJson}\r\n",
        $"if [ \"$1\" = \"auth\" ]; then\necho username=msagent\necho 'password={token}'\nexit 0\nfi\necho '{agentListJson}'\n");

    public static async Task<(T Result, string Output)> CaptureAsync<T>(Func<Task<T>> run)
    {
        var (output, error) = (Console.Out, Console.Error);
        using var writer = new StringWriter();
        Console.SetOut(writer);
        Console.SetError(writer);

        try
        {
            return (await run(), writer.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    public static string Capture(Action run) => CaptureAsync(() => { run(); return Task.FromResult(0); }).GetAwaiter().GetResult().Output;

    public static GitCli Git(ScratchDirectory scratch, Transcript transcript, string globalConfig = "[user]\n\tname = E2E Harness\n\temail = harness@example.invalid\n[commit]\n\tgpgsign = false\n[init]\n\tdefaultBranch = main\n", string executable = "git") => new(transcript, EnvironmentOverlay.Empty.Set("GIT_CONFIG_GLOBAL", scratch.Place(Guid.NewGuid().ToString("N") + ".gitconfig", globalConfig)).Set("GIT_CONFIG_NOSYSTEM", "1"), executable);

    public static Scenario AmsScenario(params Capability[] requires) => new() { Id = "AM-00", Title = "a scenario under test", Feature = AmsMcsServices.Feature, Requires = requires, Tier = SafetyTier.ReadOnly, Run = _ => Task.CompletedTask };
}

internal sealed class ScratchDirectory : IDisposable
{
    public ScratchDirectory() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "msagent-ams-" + Guid.NewGuid().ToString("N"));

    public string Place(string relative, string content = "")
    {
        var file = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return file;
    }

    public string Folder(string relative) => Directory.CreateDirectory(System.IO.Path.Combine(Path, relative)).FullName;

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            LocalDirectories.Delete(Path);
        }
    }
}

internal sealed record FakeCall(IReadOnlyList<string> Arguments, string WorkingDirectory, string? Stdin, TimeSpan Timeout, IReadOnlyDictionary<string, string?> Environment)
{
    public string Line => string.Join(' ', Arguments);
}

internal sealed class FakeMsagent
{
    private readonly Dictionary<string, Queue<MsagentResult>> _answers = new(StringComparer.Ordinal);

    public List<FakeCall> Calls { get; } = [];

    public FakeMsagent Answer(string command, string stdout, int exitCode = 0)
    {
        if (!_answers.TryGetValue(command, out var queue))
        {
            _answers[command] = queue = new Queue<MsagentResult>();
        }

        queue.Enqueue(new MsagentResult([], exitCode, stdout, exitCode == 0 ? string.Empty : $"{command} failed", TimeSpan.Zero));
        return this;
    }

    public MsagentCli Cli(Transcript transcript, string directory) => new("msagent", transcript, System.IO.Path.Combine(directory, "dumps")) { Launch = LaunchAsync };

    public IReadOnlyList<FakeCall> CallsTo(string command) => [.. Calls.Where(call => call.Line.StartsWith(command, StringComparison.Ordinal))];

    private Task<MsagentResult> LaunchAsync(ProcessStartInfo startInfo, IReadOnlyList<string> arguments, string? stdin, TimeSpan timeout, Transcript? transcript, CancellationToken cancellationToken)
    {
        Calls.Add(new FakeCall(arguments, startInfo.WorkingDirectory, stdin, timeout, new Dictionary<string, string?>(startInfo.Environment, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)));

        var line = string.Join(' ', arguments);
        var command = _answers.Keys.Where(candidate => line.StartsWith(candidate, StringComparison.Ordinal)).MaxBy(candidate => candidate.Length);
        var answer = command is null ? new MsagentResult(arguments, 0, string.Empty, string.Empty, TimeSpan.Zero) : (_answers[command].Count > 1 ? _answers[command].Dequeue() : _answers[command].Peek()) with { Arguments = arguments };

        transcript?.Record(startInfo.FileName, answer);
        return Task.FromResult(answer);
    }
}

internal sealed class FakeTokens : IAccessTokens
{
    private readonly string _token;

    public FakeTokens(string token = "fake-token") => _token = token;

    public List<string> Resources { get; } = [];

    public Exception? Failure { get; init; }

    public Action? OnToken { get; init; }

    public Task<string> TokenAsync(string resource, CancellationToken cancellationToken)
    {
        Resources.Add(resource);
        OnToken?.Invoke();
        return Failure is null ? Task.FromResult(_token) : Task.FromException<string>(Failure);
    }

    public static string Jwt(string userPrincipalName) => JwtOf($$"""{"upn":"{{userPrincipalName}}"}""");

    public static string JwtOf(string payload) => $"header.{Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.signature";

    public static string Credential(string token) => $"username=msagent\npassword={token}\n";
}

internal sealed record OracleCall(HttpMethod Method, Uri Uri, string? Body, string IfMatch, AuthenticationHeaderValue? Authorization)
{
    public string Target => $"{Method} {Uri.UnescapeDataString(Uri.AbsoluteUri)}";
}

internal sealed class OracleStub : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Fragment, Queue<(HttpStatusCode Status, string Body)> Answers)> _routes = [];
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _silent = new(StringComparer.OrdinalIgnoreCase);

    public List<OracleCall> Calls { get; } = [];

    public Action? OnCall { get; set; }

    public OracleStub Fail(string host, Exception failure)
    {
        _failures[host] = failure;
        return this;
    }

    public OracleStub Silence(string host)
    {
        _silent.Add(host);
        return this;
    }

    public OracleStub Answer(HttpMethod method, string fragment, HttpStatusCode status, string body = "")
    {
        if (_routes.FirstOrDefault(route => route.Method == method && route.Fragment == fragment).Answers is { } answers)
        {
            answers.Enqueue((status, body));
            return this;
        }

        _routes.Add((method, fragment, new Queue<(HttpStatusCode, string)>([(status, body)])));
        return this;
    }

    public IReadOnlyList<OracleCall> CallsTo(HttpMethod method, string fragment) => [.. Calls.Where(call => call.Method == method && call.Target.Contains(fragment, StringComparison.Ordinal))];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = new OracleCall(request.Method, request.RequestUri!, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.IfMatch.ToString(), request.Headers.Authorization);
        Calls.Add(call);
        OnCall?.Invoke();

        if (_silent.Contains(call.Uri.Host))
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (_failures.TryGetValue(call.Uri.Host, out var failure))
        {
            throw failure;
        }

        var route = _routes.Where(candidate => candidate.Method == call.Method && call.Target.Contains(candidate.Fragment, StringComparison.Ordinal)).OrderByDescending(candidate => candidate.Fragment.Length).FirstOrDefault();
        var (status, body) = route.Answers is null ? (HttpStatusCode.NotFound, string.Empty) : route.Answers.Count > 1 ? route.Answers.Dequeue() : route.Answers.Peek();

        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

internal sealed class VariableScope : IDisposable
{
    public static readonly string[] AmsMcsSuffixes = ["FEATURES", "DATA_PLANE_ENDPOINT", "DATA_PLANE_AUDIENCE", "ON_BEHALF_OF_ENDPOINT", "ON_BEHALF_OF_AUDIENCE", "CONTROL_PLANE_ENDPOINT", "CONTROL_PLANE_AUDIENCE", "GIT_HOST", "DEV_ENVIRONMENT_ID", "TEST_ENVIRONMENT_ID", "PROD_ENVIRONMENT_ID", "ORGANIZATIONS_DEV", "ORGANIZATIONS_TEST", "ORGANIZATIONS_PROD", "WORKSPACE_ROOT", "FORBIDDEN_ENVIRONMENT_IDS"];

    private readonly Dictionary<string, string?> _prior = [];

    public VariableScope(params string[] names)
    {
        foreach (var name in names.Concat(new[] { E2EConfig.EnvironmentPrefix, E2EConfig.LegacyEnvironmentPrefix }.SelectMany(prefix => AmsMcsSuffixes.Select(suffix => $"{prefix}AMS_MCS_{suffix}"))))
        {
            _prior[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public static void AmsMcs(string suffix, string value, string prefix = E2EConfig.EnvironmentPrefix) => Environment.SetEnvironmentVariable($"{prefix}AMS_MCS_{suffix}", value);

    public void Dispose()
    {
        foreach (var (name, value) in _prior)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
