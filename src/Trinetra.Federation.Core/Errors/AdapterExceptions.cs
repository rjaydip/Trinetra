namespace Trinetra.Federation.Core.Errors;

/// <summary>
/// Base for every adapter failure.
/// </summary>
/// <remarks>
/// The runtime reacts differently to each subtype, so adapters must throw the specific one
/// rather than a bare <see cref="Exception"/>:
/// <list type="bullet">
/// <item><see cref="TransientVmsException"/> — retry with backoff; counts toward the circuit breaker.</item>
/// <item><see cref="AuthException"/> — do not retry; needs operator action.</item>
/// <item><see cref="CapabilityException"/> — a permanent property of this VMS, not a failure.</item>
/// <item><see cref="RateLimitedException"/> — back off, honouring the vendor's own hint if given.</item>
/// </list>
/// The distinction is load-bearing rather than cosmetic: retrying a rejected credential across
/// an 80k-camera estate is exactly how an integration account gets locked out estate-wide.
/// </remarks>
public class AdapterException : Exception
{
    public AdapterException() { }

    public AdapterException(string message) : base(message) { }

    public AdapterException(string message, Exception innerException)
        : base(message, innerException) { }

    public AdapterException(string message, Guid? targetId, Exception? innerException = null)
        : base(message, innerException) => TargetId = targetId;

    /// <summary>The connector target this failure relates to, when known.</summary>
    public Guid? TargetId { get; init; }
}

/// <summary>VMS unreachable or failing in a way that may recover. Retryable.</summary>
public class TransientVmsException : AdapterException
{
    public TransientVmsException() { }
    public TransientVmsException(string message) : base(message) { }
    public TransientVmsException(string message, Exception innerException)
        : base(message, innerException) { }
    public TransientVmsException(string message, Guid? targetId, Exception? inner = null)
        : base(message, targetId, inner) { }
}

/// <summary>
/// Credentials rejected or expired. Not retryable without operator action, and deliberately
/// excluded from circuit-breaker accounting — see <see cref="AdapterException"/>.
/// </summary>
public sealed class AuthException : AdapterException
{
    public AuthException() { }
    public AuthException(string message) : base(message) { }
    public AuthException(string message, Exception innerException)
        : base(message, innerException) { }
    public AuthException(string message, Guid? targetId, Exception? inner = null)
        : base(message, targetId, inner) { }
}

/// <summary>
/// The VMS does not support this operation. A permanent fact about the target, not a failure:
/// the runtime records it and stops asking rather than retrying.
/// </summary>
public sealed class CapabilityException : AdapterException
{
    public CapabilityException() { }
    public CapabilityException(string message) : base(message) { }
    public CapabilityException(string message, Exception innerException)
        : base(message, innerException) { }

    public CapabilityException(string message, string capability, Guid? targetId = null)
        : base(message, targetId) => Capability = capability;

    public string? Capability { get; init; }
}

/// <summary>Vendor rejected us for rate. <see cref="RetryAfter"/> carries its hint when given.</summary>
public sealed class RateLimitedException : TransientVmsException
{
    public RateLimitedException() { }
    public RateLimitedException(string message) : base(message) { }
    public RateLimitedException(string message, Exception innerException)
        : base(message, innerException) { }

    public RateLimitedException(string message, TimeSpan? retryAfter, Guid? targetId = null)
        : base(message, targetId) => RetryAfter = retryAfter;

    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// A vendor payload could not be mapped onto the common schema.
/// </summary>
/// <remarks>
/// Never fatal to the connector. The raw payload is retained via <c>RawReference</c> and the
/// event is dead-lettered, so one malformed event cannot stall a target's entire stream —
/// which, on a busy site, would otherwise mean losing every subsequent event behind it.
/// </remarks>
public sealed class NormalisationException : AdapterException
{
    public NormalisationException() { }
    public NormalisationException(string message) : base(message) { }
    public NormalisationException(string message, Exception innerException)
        : base(message, innerException) { }
    public NormalisationException(string message, Guid? targetId, Exception? inner = null)
        : base(message, targetId, inner) { }
}

/// <summary>Target is misconfigured. Quarantine rather than retry.</summary>
public sealed class ConfigurationException : AdapterException
{
    public ConfigurationException() { }
    public ConfigurationException(string message) : base(message) { }
    public ConfigurationException(string message, Exception innerException)
        : base(message, innerException) { }
    public ConfigurationException(string message, Guid? targetId, Exception? inner = null)
        : base(message, targetId, inner) { }
}
