using System.Text;

namespace Microsoft.Agents.Hosting.Cli.Tests.Packaging;

/// <summary>
/// A representative project tree used by package and publication tests.
///
/// <para>
/// It is written from fixed byte literals rather than copied from the repository so tests do not
/// depend on a checkout, line-ending setting, or stray working-copy files.
/// </para>
///
/// <para>
/// The tree deliberately contains one instance of every excluded directory and file in the fixed
/// closed exclusion list, plus a <c>.gitignore</c> and a file that <c>.gitignore</c> would exclude.
/// The <c>.gitignore</c> and the file it names are both packaged: the builder must never read an
/// environment ignore-set, so an ignored file is ordinary content.
/// </para>
/// </summary>
internal static class TestProjectSource
{
    /// <summary>Files that must appear in the archive, in the order the builder must emit them.</summary>
    public static IReadOnlyList<string> ExpectedEntries =>
    [
        ".gitignore",
        "README.md",
        "UPPER.TXT",
        "agenthosting.package.v0.json",
        "data/nested/deep/values.json",
        "ignored-by-gitignore.txt",
        "src/agent.js",
        "src/lib/util.js",
        "src/lib/util.js.map",
    ];

    private static readonly (string Path, string Content)[] IncludedFiles =
    [
        (".gitignore", "ignored-by-gitignore.txt\nsrc/**/*.map\n"),
        ("README.md", "# Agent package test project\n"),
        ("UPPER.TXT", "Ordinal ordering puts an upper-case name before a lower-case one.\n"),
        ("data/nested/deep/values.json", "{\"test\":true}\n"),
        ("ignored-by-gitignore.txt", "Listed in .gitignore and packaged anyway.\n"),
        ("src/agent.js", "export function handler() {\n  return 'hello from the test agent';\n}\n"),
        ("src/lib/util.js", "export const version = '0';\n"),
        ("src/lib/util.js.map", "{\"version\":3}\n"),
    ];

    private static readonly (string Path, string Content)[] ExcludedFiles =
    [
        (".git/config", "[core]\n"),
        (".svn/entries", "12\n"),
        (".hg/hgrc", "[paths]\n"),
        ("node_modules/pkg/index.js", "module.exports = {};\n"),
        ("bin/Debug/agent.dll", "not a real assembly\n"),
        ("obj/project.assets.json", "{}\n"),
        (".vs/agent/settings.json", "{}\n"),
        (".vscode/launch.json", "{}\n"),
        (".idea/workspace.xml", "<project/>\n"),
        ("__pycache__/module.cpython-312.pyc", "bytecode\n"),
        (".venv/pyvenv.cfg", "home = /usr\n"),
        ("venv/pyvenv.cfg", "home = /usr\n"),
        ("src/nested/node_modules/dep/index.js", "module.exports = {};\n"),
        ("agent.csproj.user", "<Project/>\n"),
        ("agent.suo", "binary-ish\n"),
        (".DS_Store", "mac metadata\n"),
        ("Thumbs.db", "windows metadata\n"),
    ];

    /// <summary>Writes the representative source tree under <paramref name="root"/>.</summary>
    public static string Create(string root)
    {
        foreach (var (path, content) in IncludedFiles.Concat(ExcludedFiles))
        {
            Write(root, path, content);
        }

        return root;
    }

    /// <summary>Writes only the packaged files, with none of the excluded content present.</summary>
    public static string CreateWithoutExcludedContent(string root)
    {
        foreach (var (path, content) in IncludedFiles)
        {
            Write(root, path, content);
        }

        return root;
    }

    private static void Write(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Encoding.UTF8.GetBytes(content));
    }
}

/// <summary>A temporary directory that deletes itself, so no test leaves package output behind.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ah-package-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
