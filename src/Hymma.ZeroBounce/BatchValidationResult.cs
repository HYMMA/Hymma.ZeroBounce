namespace Hymma.ZeroBounce;

/// <summary>
/// Result of validating several addresses in one or more batch requests.
/// </summary>
public class BatchValidationResult
{
    /// <summary>
    /// One result per requested address, in request order. An address ZeroBounce
    /// gave no verdict for comes back with <see cref="EmailValidationStatus.Error"/>
    /// rather than being dropped.
    /// </summary>
    public required IReadOnlyList<EmailValidationResult> Results { get; init; }

    /// <summary>
    /// Errors ZeroBounce reported. An entry whose <see cref="BatchValidationError.AppliesToAll"/>
    /// is true is an account-level problem (rejected key, no credits), not a verdict
    /// on any one address.
    /// </summary>
    public required IReadOnlyList<BatchValidationError> Errors { get; init; }

    /// <summary>
    /// True when <see cref="Errors"/> is non-empty.
    /// </summary>
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// One error entry from a ZeroBounce batch response.
/// </summary>
public class BatchValidationError
{
    /// <summary>
    /// The address the error applies to, or <c>all</c> for the whole request.
    /// </summary>
    public required string EmailAddress { get; init; }

    /// <summary>
    /// ZeroBounce's error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// True when the error applies to the whole request rather than one address.
    /// </summary>
    public bool AppliesToAll => string.Equals(EmailAddress, "all", StringComparison.OrdinalIgnoreCase);
}
