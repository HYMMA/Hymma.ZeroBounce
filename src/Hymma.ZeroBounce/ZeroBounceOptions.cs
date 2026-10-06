namespace Hymma.ZeroBounce;

/// <summary>
/// Configuration options for the ZeroBounce client.
/// </summary>
public class ZeroBounceOptions
{
    /// <summary>
    /// The configuration section name for binding.
    /// </summary>
    public const string SectionName = "ZeroBounce";

    /// <summary>
    /// The default (global) API endpoint.
    /// </summary>
    public const string DefaultBaseUrl = "https://api.zerobounce.net/v2";

    /// <summary>
    /// The U.S.-hosted API endpoint.
    /// </summary>
    public const string UsBaseUrl = "https://api-us.zerobounce.net/v2";

    /// <summary>
    /// The EU-hosted API endpoint (for data-residency requirements).
    /// </summary>
    public const string EuBaseUrl = "https://api-eu.zerobounce.net/v2";

    /// <summary>
    /// The ZeroBounce API key.
    /// </summary>
    public required string ApiKey { get; set; }

    /// <summary>
    /// The base URL for the ZeroBounce API. Defaults to <see cref="DefaultBaseUrl"/>;
    /// use <see cref="UsBaseUrl"/> or <see cref="EuBaseUrl"/> for a regional endpoint.
    /// </summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// How long ZeroBounce may spend verifying a single address before it gives up
    /// and answers <c>unknown</c> / <c>timeout_exceeded</c>. Sent to the API as its
    /// own <c>timeout</c> parameter, so a slow mail server comes back as a real
    /// answer instead of a dropped connection. ZeroBounce accepts 3–60 seconds;
    /// values outside that range are clamped. Defaults to 30.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// How long ZeroBounce may spend on one batch request (up to 200 addresses)
    /// before answering <c>unknown</c> for the stragglers. Sent to the API as the
    /// batch <c>timeout</c> parameter; ZeroBounce accepts 10–120 seconds and values
    /// outside that range are clamped. Defaults to 120.
    /// </summary>
    public int BatchTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Whether to throw a <see cref="ZeroBounceException"/> when a request fails
    /// (network error, timeout, unparsable response, rejected API key).
    /// If false, validation returns a result with
    /// <see cref="EmailValidationStatus.Error"/> instead. Defaults to false.
    /// </summary>
    public bool ThrowOnError { get; set; } = false;

    /// <summary>
    /// Whether to include the raw JSON response in single-validation results for
    /// debugging. Defaults to false.
    /// </summary>
    public bool IncludeRawResponse { get; set; } = false;
}
