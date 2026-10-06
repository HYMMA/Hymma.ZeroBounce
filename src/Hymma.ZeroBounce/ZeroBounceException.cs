namespace Hymma.ZeroBounce;

/// <summary>
/// Exception thrown when a ZeroBounce API operation fails.
/// </summary>
public class ZeroBounceException : Exception
{
    /// <summary>
    /// A short machine-readable code for the failure, e.g. <c>timeout</c>,
    /// <c>http_error</c>, <c>parse_error</c>, <c>api_error</c>, <c>invalid_api_key</c>.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Creates a new ZeroBounce exception.
    /// </summary>
    public ZeroBounceException(string message, string code)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// Creates a new ZeroBounce exception with an inner exception.
    /// </summary>
    public ZeroBounceException(string message, string code, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }
}

/// <summary>
/// Thrown by a consumer when an address failed validation and a send was refused.
/// The library itself never throws this; it exists so callers up the stack can catch
/// "the recipient is bad" separately from "the email service is down".
/// </summary>
public class EmailValidationException : Exception
{
    /// <summary>
    /// The verdict that caused the refusal.
    /// </summary>
    public EmailValidationResult ValidationResult { get; }

    /// <summary>
    /// Creates an exception with a default message naming the address and verdict.
    /// </summary>
    public EmailValidationException(EmailValidationResult result)
        : base($"Email '{result.Email}' failed validation: {result.Status}/{result.SubStatus}")
    {
        ValidationResult = result;
    }

    /// <summary>
    /// Creates an exception with a custom message.
    /// </summary>
    public EmailValidationException(EmailValidationResult result, string message)
        : base(message)
    {
        ValidationResult = result;
    }
}
