using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class BrokerFallbackTests
{
    [Fact]
    public async Task ASuccessfulBrokerCall_ReturnsItsResult() =>
        Assert.Equal(42, await BrokerFailure.TranslateAsync(() => Task.FromResult(42)));

    [Theory]
    [InlineData("wam_runtime_init_failed")]
    [InlineData(MsalError.WamUiThread)]
    [InlineData(MsalError.UnknownBrokerError)]
    [InlineData("WAM_provider_error_3399614476")]
    public async Task AnyMsalClientOrServiceFailure_MakesTheBrokerUnavailable_OnEveryPlatform(string errorCode)
    {
        await AssertUnavailableAsync(new MsalClientException(errorCode, "The broker failed."), errorCode);
        await AssertUnavailableAsync(new MsalServiceException(errorCode, "The broker failed."), errorCode);
    }

    [Fact]
    public Task ABrokerThatHoldsNoUsableAccount_MakesTheBrokerUnavailable() =>
        AssertUnavailableAsync(
            new MsalUiRequiredException(MsalError.FailedToAcquireTokenSilentlyFromBroker, "The broker holds no usable account."),
            MsalError.FailedToAcquireTokenSilentlyFromBroker);

    [Fact]
    public Task ARetryableOutage_MakesTheBrokerUnavailable() =>
        AssertUnavailableAsync(new MsalServiceException("3", "No network.") { IsRetryable = true }, "3");

    [Fact]
    public async Task ABrokerLibraryThatCannotLoad_MakesTheBrokerUnavailable_NamedByItsExceptionType()
    {
        await AssertUnavailableAsync(new DllNotFoundException("Unable to load shared library 'msalruntime' or one of its dependencies."), "DllNotFoundException");
        await AssertUnavailableAsync(new EntryPointNotFoundException("Missing export."), "EntryPointNotFoundException");
        await AssertUnavailableAsync(new BadImageFormatException("Wrong architecture."), "BadImageFormatException");
        await AssertUnavailableAsync(new PlatformNotSupportedException("No broker here."), "PlatformNotSupportedException");
        await AssertUnavailableAsync(new TypeInitializationException("Broker", null), "TypeInitializationException");
    }

    [Fact]
    public Task TheReason_IsTheErrorCode_WithTheBrokerStatusAndCorrelationId() =>
        AssertUnavailableAsync(
            new MsalServiceException("WAM_provider_error_3399614476", "WAM Error \n Error Code: 3399614476 \n Internal Error Code: 557973642 \n")
            {
                CorrelationId = "00000000-0000-0000-0000-000000000001",
                AdditionalExceptionData = new Dictionary<string, string> { [MsalException.BrokerErrorStatus] = "AccountUnusable" },
            },
            "WAM_provider_error_3399614476, broker status AccountUnusable, correlation ID 00000000-0000-0000-0000-000000000001");

    [Fact]
    public Task TheReason_LeavesOutMsalsMessage_SoALibraryThatCannotLoadIsNotNamed() =>
        AssertUnavailableAsync(
            new MsalClientException("wam_runtime_init_failed", "Unable to load shared library 'msalruntime' or one of its dependencies. In order to help diagnose loading problems, consider using a tool like strace.\nlibwebkit2gtk-4.1.so.0: cannot open shared object file: No such file or directory See https://aka.ms/msal-net-wam#troubleshooting")
            {
                CorrelationId = "00000000-0000-0000-0000-000000000002",
            },
            "wam_runtime_init_failed, correlation ID 00000000-0000-0000-0000-000000000002");

    [Fact]
    public Task TheReason_LeavesOutABlankBrokerStatusAndCorrelationId() =>
        AssertUnavailableAsync(
            new MsalServiceException("WAM_provider_error_3399614476", "WAM Error")
            {
                CorrelationId = " ",
                AdditionalExceptionData = new Dictionary<string, string> { [MsalException.BrokerErrorStatus] = " " },
            },
            "WAM_provider_error_3399614476");

    [Fact]
    public async Task AnOperatorCancellation_StopsSignIn_InsteadOfFallingBack()
    {
        var canceled = await Assert.ThrowsAsync<SignInCanceledException>(() => BrokerFailure.TranslateAsync<int>(() => throw new MsalClientException(MsalError.AuthenticationCanceledError, "User canceled authentication.")));

        Assert.Equal("Sign-in was canceled (authentication_canceled).", canceled.Message);
        Assert.Equal(ExitCodes.Failure, canceled.ExitCode);
        Assert.Contains("msagent auth create", canceled.Remediation);
        Assert.Contains("--deviceCode", canceled.Remediation);
    }

    [Fact]
    public async Task AFailureOutsideTheBroker_PropagatesUnchanged()
    {
        var unexpected = new InvalidOperationException("Unexpected.");
        var canceled = new OperationCanceledException();

        Assert.Same(unexpected, await Assert.ThrowsAsync<InvalidOperationException>(() => BrokerFailure.TranslateAsync<int>(() => throw unexpected)));
        Assert.Same(canceled, await Assert.ThrowsAsync<OperationCanceledException>(() => BrokerFailure.TranslateAsync<int>(() => throw canceled)));
    }

    private static async Task AssertUnavailableAsync(Exception failure, string reason)
    {
        var unavailable = await Assert.ThrowsAsync<BrokerUnavailableException>(() => BrokerFailure.TranslateAsync<int>(() => throw failure));

        Assert.Equal(reason, unavailable.Reason);
        Assert.Equal($"The operating-system broker could not sign you in ({reason}).", unavailable.Message);
        Assert.Equal(ExitCodes.Failure, unavailable.ExitCode);
        Assert.Contains("msagent auth create --deviceCode", unavailable.Remediation);
    }
}

public sealed class BrokerFallbackNoticeTests
{
    [Fact]
    public void TheNoticeNamesTheBrokerFailure() =>
        Assert.Equal(
            "Broker sign-in failed (wam_runtime_init_failed, correlation ID 00000000-0000-0000-0000-000000000002), so msagent will use a device code.",
            BrokerFallbackNotice.Create("wam_runtime_init_failed, correlation ID 00000000-0000-0000-0000-000000000002"));

    [Fact]
    public void TheNoticeRequiresTheReason() =>
        Assert.Throws<ArgumentNullException>(() => BrokerFallbackNotice.Create(null!));
}
