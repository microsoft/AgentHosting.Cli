namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>
/// Pure decision for whether ANSI color should be emitted. Kept side-effect free (no environment or
/// console access) so it is directly unit-testable. The composition root supplies the real inputs;
/// mirrors the TypeScript implementation, which suppresses color for <c>--no-color</c>,
/// <c>NO_COLOR</c>, and non-TTY output.
/// </summary>
public static class ConsoleColorPolicy
{
    /// <summary>
    /// Returns whether color should be emitted. Color is suppressed when <paramref name="noColorFlag"/>
    /// (<c>--no-color</c>) is set, when <paramref name="noColorEnv"/> (<c>NO_COLOR</c>) is present and
    /// non-empty, or when both stdout and stderr are redirected (piped/non-TTY).
    /// </summary>
    public static bool ShouldEmitColor(bool noColorFlag, string? noColorEnv, bool outputRedirected, bool errorRedirected)
    {
        if (noColorFlag)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(noColorEnv))
        {
            return false;
        }

        if (outputRedirected && errorRedirected)
        {
            return false;
        }

        return true;
    }
}
