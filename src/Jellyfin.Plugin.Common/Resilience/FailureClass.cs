namespace Jellyfin.Plugin.Common.Resilience;

/// <summary>
/// Why a call to an external service failed. Each class has its own retry behaviour and alerting, because a missing
/// network, a busy server and an exhausted monthly budget need very different responses.
/// </summary>
internal enum FailureClass
{
    /// <summary>No network connection at all. Wait until connectivity returns (checked cheaply); alert straight away.</summary>
    NoConnection = 0,

    /// <summary>Timeouts, server errors, short rate limits. Retry quickly with a jittered back-off; alert only if it persists.</summary>
    Transient,

    /// <summary>The provider's quota, credit or spending limit is used up. Trust its reset time; otherwise back off for hours to days.</summary>
    ProviderLimit,

    /// <summary>Bad or revoked credentials, or no permission. Never retried; the user has to fix the configuration.</summary>
    Authentication,

    /// <summary>The request itself was rejected (bad input, unsupported file). Only that item fails; the queue carries on.</summary>
    BadRequest,
}
