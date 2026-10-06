# Hymma.ZeroBounce

> ⚠️ **DISCLAIMER**: This is NOT an official ZeroBounce package. It is a community-maintained .NET client library created by [HYMMA](https://github.com/HYMMA). For the official SDK and support, visit [ZeroBounce](https://www.zerobounce.net/).

A modern .NET 8 client for the [ZeroBounce](https://www.zerobounce.net/) email validation API (v2). Validate addresses before you send so you stop paying for bounces with your sender reputation.

## Why not the official SDK?

The official `ZeroBounce.SDK` is a static singleton with callback-style methods that block on `.Wait()` internally. Inside an ASP.NET Core request that is sync-over-async: it ties up a thread-pool thread per call and can starve the pool under load. It also targets `netstandard2.0`, depends on Newtonsoft.Json, has no dependency-injection story, and never passes ZeroBounce's own `timeout` parameter.

This library is `async` end to end, built on `IHttpClientFactory`, registered with one line, uses `System.Text.Json`, and tells ZeroBounce how long it may spend on each address.

## Features

- **Single validation** — `ValidateAsync`, with optional sign-up IP for enrichment
- **Batch validation** — `ValidateBatchAsync`, chunked at the API limit, request order preserved, nothing silently dropped
- **Credit balance** — `GetCreditsAsync`
- **Typed verdicts** — every `status` and `sub_status` ZeroBounce documents, as enums, with the raw strings kept alongside
- **Three-way partition** — `IsSafeToSend` / `IsConfirmedUndeliverable` / `IsInconclusive`, so "we don't know" is never mistaken for "bad"
- **Fail-open by default** — network trouble comes back as `Status = Error`, never as a thrown exception, unless you ask for `ThrowOnError`
- **Caller cancellation is honoured** — your `CancellationToken` propagates as `OperationCanceledException`; it is never reported as a ZeroBounce failure
- **Regional endpoints** — global, U.S. and EU base URLs as constants
- **Key stays out of URLs** — every request carries the API key in a POST body, so HttpClientFactory's request-URI log lines and OpenTelemetry's `url.full` never contain it

## Installation

```bash
dotnet add package Hymma.ZeroBounce
```

## Quick start

### 1. Register

```csharp
// API key directly
builder.Services.AddZeroBounce("your_api_key");

// From configuration — reads the "ZeroBounce" section
builder.Services.AddZeroBounce(builder.Configuration);

// Full control
builder.Services.AddZeroBounce(options =>
{
    options.ApiKey = "your_api_key";
    options.BaseUrl = ZeroBounceOptions.EuBaseUrl;
    options.TimeoutSeconds = 10;
});
```

```json
// appsettings.json (for the configuration overload)
{
  "ZeroBounce": {
    "ApiKey": "your_api_key",
    "TimeoutSeconds": 10
  }
}
```

### 2. Validate

```csharp
public class SignupService(IZeroBounceClient zeroBounce)
{
    public async Task<SignupOutcome> SignUpAsync(string email, string clientIp, CancellationToken ct)
    {
        var result = await zeroBounce.ValidateAsync(email, clientIp, ct);

        // Block only on what ZeroBounce is sure about.
        if (result.IsConfirmedUndeliverable)
        {
            var hint = result.DidYouMean is { } fix ? $" Did you mean {fix}?" : "";
            return SignupOutcome.Rejected($"We can't deliver to {email}.{hint}");
        }

        // Unknown / Error: ZeroBounce could not reach a verdict (or we could not
        // reach ZeroBounce). Let the sign-up through — a false reject costs you a
        // real customer; a bounce costs you a little reputation.
        if (result.IsInconclusive)
            logger.LogWarning("ZeroBounce inconclusive for {Email}: {Status}/{Sub}", email, result.Status, result.SubStatus);

        return SignupOutcome.Accepted;
    }
}
```

## Verdicts

ZeroBounce returns a `status` and a `sub_status`. The library maps both to enums and partitions every status into exactly one bucket:

| `Status` | `IsSafeToSend` | `IsConfirmedUndeliverable` | `IsInconclusive` | Meaning |
|---|:-:|:-:|:-:|---|
| `Valid` | ✅ | | | Deliverable mailbox |
| `CatchAll` | ✅ | | | Domain accepts any local part; mailbox unverifiable |
| `Invalid` | | ⛔ | | Will bounce — mailbox missing, full, bad syntax, no DNS… |
| `SpamTrap` | | ⛔ | | Known trap. Never send |
| `Abuse` | | ⛔ | | Known complainer. Never send |
| `DoNotMail` | | ⛔ | | Disposable, toxic, role-based or globally suppressed |
| `Unknown` | | | ❓ | Mail server didn't answer (greylisting, anti-spam, timeout) |
| `Error` | | | ❓ | The request itself failed (network, timeout, bad response, rejected key) |

Sub-status shortcuts on the result:

```csharp
result.IsMailboxFull      // mailbox_quota_exceeded — soft-bounces until the owner cleans up
result.IsMailboxNotFound  // mailbox_not_found
result.IsDisposable       // disposable
result.IsToxic            // toxic
result.IsRoleBased        // role_based or role_based_catch_all
result.HasBadSyntax       // failed_syntax_check
result.TimedOut           // timeout_exceeded — raise TimeoutSeconds if you see this a lot
result.SubStatus          // the full enum
result.RawSubStatus       // the exact string, for anything the enum doesn't know yet
```

Other fields: `Account`, `Domain`, `DidYouMean`, `IsFreeEmail`, `MxFound`, `MxRecord`, `SmtpProvider`, `IsCatchAllDomain`, `DomainAgeDays`, `ProcessedAt`, `ErrorMessage`, `RawResponse`.

## Batch validation

```csharp
var batch = await zeroBounce.ValidateBatchAsync(leads.Select(l => l.Email), ct);

foreach (var r in batch.Results)          // one per input address, same order
    if (r.IsConfirmedUndeliverable) await leads.SuppressAsync(r.Email, r.SubStatus);

if (batch.Errors.Any(e => e.AppliesToAll)) // rejected key / no credits
    logger.LogError("ZeroBounce batch failed: {Error}", batch.Errors[0].Message);
```

Requests are sent in chunks of 200. Blank entries are skipped. An address ZeroBounce does not answer for comes back as `Status = Error` rather than disappearing, so `Results.Count` always matches the non-blank input count.

## Timeouts

ZeroBounce accepts a per-request `timeout`; when it runs out the API answers `unknown` / `timeout_exceeded` instead of leaving you hanging. `TimeoutSeconds` (3–60, default 30) is sent on every single check, and `BatchTimeoutSeconds` (10–120, default 120) on every batch. The HTTP call itself is allowed a few seconds more than that, so a server-side timeout arrives as a real verdict rather than a dropped connection.

Pick `TimeoutSeconds` by where the call sits: a sign-up form that blocks on it wants ~8–10s; a background re-validation job can afford the full 60.

## Error handling

### Default — fail open

```csharp
var result = await client.ValidateAsync(email);

if (result.Status == EmailValidationStatus.Error)
    // network error, timeout, unparsable body, or the API said
    // {"error":"Invalid API key or your account ran out of credits"}
    logger.LogError("ZeroBounce unavailable: {Message}", result.ErrorMessage);
```

The one deliberate exception is `GetCreditsAsync`, which always throws — there is no honest fallback value for a balance.

### Strict — `ThrowOnError = true`

```csharp
try
{
    var result = await client.ValidateAsync(email);
}
catch (ZeroBounceException ex) when (ex.Code is "timeout" or "http_error")
{
    // transient — retry or fail open
}
catch (ZeroBounceException ex) // "api_error", "parse_error", "invalid_api_key"
{
    // configuration problem — page someone
}
```

## Options

```csharp
services.AddZeroBounce(options =>
{
    options.ApiKey = "…";                               // required
    options.BaseUrl = ZeroBounceOptions.DefaultBaseUrl; // or UsBaseUrl / EuBaseUrl
    options.TimeoutSeconds = 30;                        // 3–60, sent to the API
    options.BatchTimeoutSeconds = 120;                  // 10–120, sent to the API
    options.ThrowOnError = false;                       // true → ZeroBounceException instead of Status = Error
    options.IncludeRawResponse = false;                 // true → RawResponse on single-validation results
});
```

## API quirks this library absorbs

Observed against the live API, so you don't have to:

- Errors come back as **HTTP 200** — `{"error": "…"}` on `validate`, `{"errors":[{"email_address":"all",…}]}` on `validatebatch`, and `{"Credits":"-1"}` on `getcredits`.
- `validate` and `getcredits` take the key in a **form-encoded** POST body (so it stays out of your request logs); a JSON body gets `415` / `500` and a header gets a Cloudflare `403`. `validatebatch` is JSON-only.
- `mx_found` is the **string** `"true"`/`"false"`; `free_email` is a real boolean; `catchall_domain` may be `null`.
- `Credits` and `domain_age_days` are strings (`domain_age_days` may be `""`).
- `processed_at` is `"yyyy-MM-dd HH:mm:ss.fff"` with no zone designator (UTC).

## API reference

- [Validate (single)](https://www.zerobounce.net/docs/email-validation-api-quickstart/v2-validate-emails)
- [Validate (batch)](https://www.zerobounce.net/docs/email-validation-api-quickstart/v2-batch-validate-emails)
- [Credit balance](https://www.zerobounce.net/docs/email-validation-api-quickstart/v2-credit-balance)

## License

MIT — see [LICENSE](LICENSE).
