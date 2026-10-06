using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hymma.ZeroBounce;

/// <summary>
/// Client implementation for the ZeroBounce email validation API (v2).
/// </summary>
public class ZeroBounceClient : IZeroBounceClient
{
    /// <summary>
    /// The most addresses the batch endpoint accepts in one request.
    /// </summary>
    public const int MaxBatchSize = 200;

    // How much longer than the API's own timeout the HTTP call may take, so a
    // server-side "timeout_exceeded" verdict arrives as a real answer instead of
    // the client giving up first.
    private const int TimeoutGraceSeconds = 5;

    private const int CreditsBudgetSeconds = 30;

    private readonly HttpClient _httpClient;
    private readonly ZeroBounceOptions _options;
    private readonly ILogger<ZeroBounceClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new LenientBooleanConverter() }
    };

    private static readonly string[] ProcessedAtFormats =
    [
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss"
    ];

    /// <summary>
    /// Creates a new instance of the ZeroBounce client.
    /// </summary>
    public ZeroBounceClient(
        HttpClient httpClient,
        IOptions<ZeroBounceOptions> options,
        ILogger<ZeroBounceClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ArgumentException("ZeroBounceOptions.ApiKey is required.", nameof(options));

        _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        // Each call gets its own budget (see SendAsync) — the single-check budget
        // follows the API timeout, the batch budget is separate.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    // ZeroBounce accepts 3–60; anything else is a 400.
    private int ApiTimeoutSeconds => Math.Clamp(_options.TimeoutSeconds, 3, 60);

    // The batch endpoint has its own range: 10–120.
    private int BatchApiTimeoutSeconds => Math.Clamp(_options.BatchTimeoutSeconds, 10, 120);

    /// <inheritdoc />
    public Task<EmailValidationResult> ValidateAsync(string email, CancellationToken cancellationToken = default) =>
        ValidateAsync(email, ipAddress: null, cancellationToken);

    /// <inheritdoc />
    public async Task<EmailValidationResult> ValidateAsync(string email, string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return new EmailValidationResult
            {
                Email = email ?? string.Empty,
                Status = EmailValidationStatus.Invalid,
                SubStatus = EmailValidationSubStatus.FailedSyntaxCheck,
                RawStatus = "invalid",
                RawSubStatus = "failed_syntax_check"
            };
        }

        // The key travels in the form body, never the URL: HttpClientFactory's
        // default logger prints every request URI at Information level, and
        // OpenTelemetry's url.full tag keeps the query string on .NET 8. ZeroBounce
        // rejects a JSON body here with 415 and a header with 403; form-encoding works.
        var form = new Dictionary<string, string>
        {
            ["api_key"] = _options.ApiKey,
            ["email"] = email,
            ["timeout"] = ApiTimeoutSeconds.ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(ipAddress))
            form["ip_address"] = ipAddress;

        _logger.LogDebug("Validating {Email} via ZeroBounce", email);

        using var request = new HttpRequestMessage(HttpMethod.Post, "validate") { Content = new FormUrlEncodedContent(form) };
        var (body, error) = await SendAsync(request, ApiTimeoutSeconds + TimeoutGraceSeconds, $"validating {email}", cancellationToken);
        if (body is null)
            return CreateErrorResult(email, error, rawResponse: null);

        var dto = Parse<ValidateResponse>(body, $"validating {email}", out error);
        if (dto is null)
            return CreateErrorResult(email, error, body);

        if (!string.IsNullOrEmpty(dto.Error))
        {
            // ZeroBounce answers HTTP 200 with {"error": "..."} for a rejected key or
            // an empty credit balance. That is an account problem, not a verdict on
            // this address, so it is logged as an error and surfaced as Status=Error.
            _logger.LogError("ZeroBounce rejected the request while validating {Email}: {Error}", email, dto.Error);
            if (_options.ThrowOnError)
                throw new ZeroBounceException($"ZeroBounce API error: {dto.Error}", "api_error");
            return CreateErrorResult(email, dto.Error, body);
        }

        var result = MapResult(dto, email, _options.IncludeRawResponse ? body : null);

        _logger.LogInformation(
            "ZeroBounce verdict for {Email}: {Status}/{SubStatus} (IsSafeToSend={IsSafeToSend})",
            email, result.Status, result.SubStatus, result.IsSafeToSend);

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> IsSafeToSendAsync(string email, CancellationToken cancellationToken = default)
    {
        var result = await ValidateAsync(email, cancellationToken);
        return result.IsSafeToSend;
    }

    /// <inheritdoc />
    public async Task<BatchValidationResult> ValidateBatchAsync(IEnumerable<string> emails, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emails);

        var addresses = emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
        var results = new List<EmailValidationResult>(addresses.Count);
        var errors = new List<BatchValidationError>();

        foreach (var chunk in addresses.Chunk(MaxBatchSize))
        {
            var payload = new BatchRequest
            {
                ApiKey = _options.ApiKey,
                EmailBatch = chunk.Select(e => new BatchRequestItem { EmailAddress = e }).ToList(),
                Timeout = BatchApiTimeoutSeconds
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "validatebatch")
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };

            var operation = $"validating a batch of {chunk.Length}";
            var (body, error) = await SendAsync(request, BatchApiTimeoutSeconds + TimeoutGraceSeconds, operation, cancellationToken);
            var dto = body is null ? null : Parse<BatchResponse>(body, operation, out error);

            if (dto is null)
            {
                var message = error ?? "ZeroBounce request failed";
                errors.Add(new BatchValidationError { EmailAddress = "all", Message = message });
                results.AddRange(chunk.Select(e => CreateErrorResult(e, message, rawResponse: null)));
                continue;
            }

            string? fatal = null;
            foreach (var err in dto.Errors ?? [])
            {
                var entry = new BatchValidationError
                {
                    EmailAddress = err.EmailAddress ?? "all",
                    Message = err.Error ?? "Unknown error"
                };
                errors.Add(entry);
                if (entry.AppliesToAll)
                    fatal ??= entry.Message;
            }

            if (fatal is not null)
            {
                // Same shape as the single-check {"error": ...}: rejected key or no
                // credits. The batch comes back empty, so every address in it is
                // reported as Error below.
                _logger.LogError("ZeroBounce rejected a batch of {Count}: {Error}", chunk.Length, fatal);
                if (_options.ThrowOnError)
                    throw new ZeroBounceException($"ZeroBounce API error: {fatal}", "api_error");
            }

            var answered = new Dictionary<string, EmailValidationResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in dto.EmailBatch ?? [])
            {
                if (string.IsNullOrEmpty(item.Address))
                    continue;
                answered[item.Address] = MapResult(item, item.Address, rawResponse: null);
            }

            // Keep request order, and never silently drop an address ZeroBounce
            // did not answer for.
            foreach (var address in chunk)
            {
                results.Add(answered.TryGetValue(address, out var r)
                    ? r
                    : CreateErrorResult(address, fatal ?? "ZeroBounce returned no verdict for this address", rawResponse: null));
            }
        }

        _logger.LogInformation(
            "ZeroBounce batch of {Count}: {Valid} valid, {Undeliverable} undeliverable, {Inconclusive} inconclusive, {Errors} errors",
            results.Count,
            results.Count(r => r.Status == EmailValidationStatus.Valid),
            results.Count(r => r.IsConfirmedUndeliverable),
            results.Count(r => r.IsInconclusive),
            errors.Count);

        return new BatchValidationResult { Results = results, Errors = errors };
    }

    /// <inheritdoc />
    public async Task<long> GetCreditsAsync(CancellationToken cancellationToken = default)
    {
        // Same reasoning as ValidateAsync: form body, so the key never appears in a URI.
        using var request = new HttpRequestMessage(HttpMethod.Post, "getcredits")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["api_key"] = _options.ApiKey })
        };
        const string operation = "reading the credit balance";

        var (body, error) = await SendAsync(request, CreditsBudgetSeconds, operation, cancellationToken);
        if (body is null)
            throw new ZeroBounceException(error ?? "ZeroBounce request failed", "request_failed");

        var dto = Parse<CreditsResponse>(body, operation, out error)
                  ?? throw new ZeroBounceException(error ?? "ZeroBounce returned an unreadable response", "parse_error");

        if (!long.TryParse(dto.Credits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var credits))
            throw new ZeroBounceException($"ZeroBounce returned an unreadable credit balance: '{dto.Credits}'", "parse_error");

        // The API signals a rejected key with a balance of -1 rather than an error.
        if (credits < 0)
            throw new ZeroBounceException("ZeroBounce rejected the API key.", "invalid_api_key");

        return credits;
    }

    /// <summary>
    /// Sends one request under a wall-clock budget. Returns the body, or null plus a
    /// message when the failure was swallowed (<see cref="ZeroBounceOptions.ThrowOnError"/>
    /// off). Caller cancellation is always propagated — it is never reported as a
    /// ZeroBounce failure.
    /// </summary>
    private async Task<(string? Body, string? Error)> SendAsync(
        HttpRequestMessage request, int budgetSeconds, string operation, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));

        try
        {
            using var response = await _httpClient.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsStringAsync(cts.Token), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, "ZeroBounce did not answer within {Seconds}s while {Operation}", budgetSeconds, operation);
            return Fail($"ZeroBounce request timed out after {budgetSeconds}s", "timeout", ex);
        }
        catch (HttpRequestException ex)
        {
            // HttpRequestException.Message carries the status code, never the URL
            // or body, so the API key cannot leak through this log line.
            _logger.LogError(ex, "HTTP error while {Operation} with ZeroBounce", operation);
            return Fail($"HTTP error calling ZeroBounce: {ex.Message}", "http_error", ex);
        }
    }

    private (string? Body, string? Error) Fail(string message, string code, Exception inner)
    {
        if (_options.ThrowOnError)
            throw new ZeroBounceException(message, code, inner);
        return (null, message);
    }

    private T? Parse<T>(string body, string operation, out string? error) where T : class
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(body, JsonOptions);
            if (value is not null)
            {
                error = null;
                return value;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Could not parse the ZeroBounce response while {Operation}", operation);
            if (_options.ThrowOnError)
                throw new ZeroBounceException($"Failed to parse ZeroBounce response: {ex.Message}", "parse_error", ex);
            error = $"Failed to parse ZeroBounce response: {ex.Message}";
            return null;
        }

        _logger.LogWarning("ZeroBounce returned an empty response while {Operation}", operation);
        error = "ZeroBounce returned an empty response";
        if (_options.ThrowOnError)
            throw new ZeroBounceException(error, "empty_response");
        return null;
    }

    private static EmailValidationResult CreateErrorResult(string email, string? error, string? rawResponse) => new()
    {
        Email = email,
        Status = EmailValidationStatus.Error,
        ErrorMessage = error,
        RawResponse = rawResponse
    };

    private static EmailValidationResult MapResult(ValidateResponse dto, string requestedEmail, string? rawResponse) => new()
    {
        Email = string.IsNullOrEmpty(dto.Address) ? requestedEmail : dto.Address,
        Status = ZeroBounceStatusMapper.ToStatus(dto.Status),
        SubStatus = ZeroBounceStatusMapper.ToSubStatus(dto.SubStatus),
        RawStatus = dto.Status,
        RawSubStatus = dto.SubStatus,
        Account = NullIfEmpty(dto.Account),
        Domain = NullIfEmpty(dto.Domain),
        DidYouMean = NullIfEmpty(dto.DidYouMean),
        IsFreeEmail = dto.FreeEmail ?? false,
        MxFound = dto.MxFound ?? false,
        MxRecord = NullIfEmpty(dto.MxRecord),
        SmtpProvider = NullIfEmpty(dto.SmtpProvider),
        IsCatchAllDomain = dto.CatchAllDomain,
        DomainAgeDays = int.TryParse(dto.DomainAgeDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ? days : null,
        ProcessedAt = ParseProcessedAt(dto.ProcessedAt),
        RawResponse = rawResponse
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // ZeroBounce stamps "2026-10-06 00:26:48.484" — no zone designator, UTC by contract.
    private static DateTimeOffset? ParseProcessedAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        if (DateTime.TryParseExact(value, ProcessedAtFormats, CultureInfo.InvariantCulture, styles, out var exact))
            return new DateTimeOffset(exact, TimeSpan.Zero);

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, styles, out var parsed) ? parsed : null;
    }

    #region API wire models

    private sealed class ValidateResponse
    {
        [JsonPropertyName("address")] public string? Address { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("sub_status")] public string? SubStatus { get; set; }
        [JsonPropertyName("account")] public string? Account { get; set; }
        [JsonPropertyName("domain")] public string? Domain { get; set; }
        [JsonPropertyName("did_you_mean")] public string? DidYouMean { get; set; }
        [JsonPropertyName("free_email")] public bool? FreeEmail { get; set; }
        [JsonPropertyName("mx_found")] public bool? MxFound { get; set; }
        [JsonPropertyName("mx_record")] public string? MxRecord { get; set; }
        [JsonPropertyName("smtp_provider")] public string? SmtpProvider { get; set; }
        [JsonPropertyName("catchall_domain")] public bool? CatchAllDomain { get; set; }
        [JsonPropertyName("domain_age_days")] public string? DomainAgeDays { get; set; }
        [JsonPropertyName("processed_at")] public string? ProcessedAt { get; set; }

        // Only present on the single-check endpoint's account-level failure.
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    private sealed class BatchRequest
    {
        [JsonPropertyName("api_key")] public required string ApiKey { get; set; }
        [JsonPropertyName("email_batch")] public required List<BatchRequestItem> EmailBatch { get; set; }
        [JsonPropertyName("timeout")] public int Timeout { get; set; }
    }

    private sealed class BatchRequestItem
    {
        [JsonPropertyName("email_address")] public required string EmailAddress { get; set; }
        [JsonPropertyName("ip_address")] public string? IpAddress { get; set; }
    }

    private sealed class BatchResponse
    {
        [JsonPropertyName("email_batch")] public List<ValidateResponse>? EmailBatch { get; set; }
        [JsonPropertyName("errors")] public List<BatchError>? Errors { get; set; }
    }

    private sealed class BatchError
    {
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("email_address")] public string? EmailAddress { get; set; }
    }

    private sealed class CreditsResponse
    {
        [JsonPropertyName("Credits")] public string? Credits { get; set; }
    }

    #endregion
}

/// <summary>
/// Maps ZeroBounce's snake_case status strings onto the library enums.
/// </summary>
internal static class ZeroBounceStatusMapper
{
    public static EmailValidationStatus ToStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "valid" => EmailValidationStatus.Valid,
        "invalid" => EmailValidationStatus.Invalid,
        "catch-all" or "catchall" => EmailValidationStatus.CatchAll,
        "unknown" => EmailValidationStatus.Unknown,
        "spamtrap" => EmailValidationStatus.SpamTrap,
        "abuse" => EmailValidationStatus.Abuse,
        "do_not_mail" => EmailValidationStatus.DoNotMail,
        _ => EmailValidationStatus.Error
    };

    public static EmailValidationSubStatus ToSubStatus(string? subStatus) => subStatus?.Trim().ToLowerInvariant() switch
    {
        null or "" => EmailValidationSubStatus.None,
        "alternate" => EmailValidationSubStatus.Alternate,
        "antispam_system" => EmailValidationSubStatus.AntispamSystem,
        "greylisted" => EmailValidationSubStatus.Greylisted,
        "mail_server_temporary_error" => EmailValidationSubStatus.MailServerTemporaryError,
        "forcible_disconnect" => EmailValidationSubStatus.ForcibleDisconnect,
        "mail_server_did_not_respond" => EmailValidationSubStatus.MailServerDidNotRespond,
        "timeout_exceeded" => EmailValidationSubStatus.TimeoutExceeded,
        "failed_smtp_connection" => EmailValidationSubStatus.FailedSmtpConnection,
        "mailbox_quota_exceeded" => EmailValidationSubStatus.MailboxQuotaExceeded,
        "exception_occurred" => EmailValidationSubStatus.ExceptionOccurred,
        "possible_trap" => EmailValidationSubStatus.PossibleTrap,
        "role_based" => EmailValidationSubStatus.RoleBased,
        "global_suppression" => EmailValidationSubStatus.GlobalSuppression,
        "mailbox_not_found" => EmailValidationSubStatus.MailboxNotFound,
        "no_dns_entries" => EmailValidationSubStatus.NoDnsEntries,
        "failed_syntax_check" => EmailValidationSubStatus.FailedSyntaxCheck,
        "possible_typo" => EmailValidationSubStatus.PossibleTypo,
        "unroutable_ip_address" => EmailValidationSubStatus.UnroutableIpAddress,
        "leading_period_removed" => EmailValidationSubStatus.LeadingPeriodRemoved,
        "does_not_accept_mail" => EmailValidationSubStatus.DoesNotAcceptMail,
        "alias_address" => EmailValidationSubStatus.AliasAddress,
        "role_based_catch_all" => EmailValidationSubStatus.RoleBasedCatchAll,
        "disposable" => EmailValidationSubStatus.Disposable,
        "toxic" => EmailValidationSubStatus.Toxic,
        "accept_all" => EmailValidationSubStatus.AcceptAll,
        "gold" => EmailValidationSubStatus.Gold,
        _ => EmailValidationSubStatus.Other
    };
}

/// <summary>
/// ZeroBounce is inconsistent about booleans: <c>free_email</c> is a real boolean,
/// <c>mx_found</c> is the string <c>"true"</c>, and <c>catchall_domain</c> may be null.
/// This converter accepts all of those.
/// </summary>
internal sealed class LenientBooleanConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.String => bool.TryParse(reader.GetString(), out var parsed) ? parsed : null,
            JsonTokenType.Number => reader.TryGetInt32(out var number) ? number != 0 : null,
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for a boolean value.")
        };

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteBooleanValue(value.Value);
    }
}
