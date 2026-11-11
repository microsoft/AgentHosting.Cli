namespace Microsoft.Agents.Hosting.Cli.Core;

internal static class FileSystemRetry
{
    private const int Attempts = 20;

    private const int AccessDenied = unchecked((int)0x80070005);

    private const int SharingViolation = unchecked((int)0x80070020);

    private const int LockViolation = unchecked((int)0x80070021);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(50);

    public static void WhileHeld(Action change)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                change();

                return;
            }
            catch (Exception failure) when (attempt < Attempts && IsHeld(failure))
            {
                Thread.Sleep(RetryInterval);
            }
        }
    }

    private static bool IsHeld(Exception failure) => failure is UnauthorizedAccessException || (failure is IOException && failure.HResult is AccessDenied or SharingViolation or LockViolation);
}
