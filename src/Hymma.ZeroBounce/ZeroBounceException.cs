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
