namespace Hymma.ZeroBounce;

/// <summary>
/// Client interface for the ZeroBounce email validation API (v2).
/// </summary>
public interface IZeroBounceClient
{
    /// <summary>
    /// Validates a single email address. Costs one credit.
    /// </summary>
    /// <param name="email">The email address to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The validation result.</returns>
    Task<EmailValidationResult> ValidateAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a single email address, passing the sign-up IP so ZeroBounce can
    /// enrich the result with location data. Costs one credit.
    /// </summary>
    /// <param name="email">The email address to validate.</param>
    /// <param name="ipAddress">The IP address the sign-up came from, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The validation result.</returns>
    Task<EmailValidationResult> ValidateAsync(string email, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience wrapper: true when <see cref="EmailValidationResult.IsSafeToSend"/>
    /// (valid or catch-all).
    /// </summary>
    /// <param name="email">The email address to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> IsSafeToSendAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates many addresses. Requests are sent in chunks of up to 200 (the API
    /// limit); blank entries are skipped. Costs one credit per address.
    /// </summary>
    /// <param name="emails">The email addresses to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per address plus any errors ZeroBounce reported.</returns>
    Task<BatchValidationResult> ValidateBatchAsync(IEnumerable<string> emails, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the number of validation credits left on the account. Free.
    /// Always throws <see cref="ZeroBounceException"/> on failure, regardless of
    /// <see cref="ZeroBounceOptions.ThrowOnError"/>, since there is no sensible fallback value.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<long> GetCreditsAsync(CancellationToken cancellationToken = default);
}
