namespace Microsoft.Agents.Hosting.Cli.Core.Packaging;

/// <summary>
/// Archive entry naming for version zero.
///
/// <para>
/// Names are UTF-8, forward-slash separated, and relative to the archive root. That form is a
/// <b>determinism</b> requirement: the same source tree must produce the same entry names, and
/// therefore the same digest, on a machine whose local separator is a backslash.
/// </para>
///
/// </summary>
public static class PackagePathPolicy
{
    /// <summary>
    /// Converts <paramref name="relativePath"/> — a path relative to the package source root, in the
    /// local platform's separator convention — to its archive name.
    /// </summary>
    public static string NormalizeSourcePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        return relativePath.Replace('\\', '/');
    }
}
