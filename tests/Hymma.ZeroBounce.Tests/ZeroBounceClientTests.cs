using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RichardSzalay.MockHttp;
using System.Net;

namespace Hymma.ZeroBounce.Tests;

public class ZeroBounceClientTests
{
    private const string Validate = "https://api.zerobounce.net/v2/validate*";
    private const string ValidateBatch = "https://api.zerobounce.net/v2/validatebatch";
    private const string GetCredits = "https://api.zerobounce.net/v2/getcredits*";

    // Captured live on 2026-10-06 — note mx_found is a string and domain_age_days
    // is a string, exactly as the API sends them.
    private const string QuotaExceededJson = """
        {"address":"full@gmail.com","status":"invalid","sub_status":"mailbox_quota_exceeded","free_email":true,"catchall_domain":false,"did_you_mean":null,"account":"full","domain":"gmail.com","domain_age_days":"11377","smtp_provider":"google","mx_found":"true","mx_record":"alt1.gmail-smtp-in.l.google.com","firstname":null,"lastname":null,"gender":null,"country":null,"region":null,"city":null,"zipcode":null,"processed_at":"2026-10-06 00:26:48.484"}
        """;

    private readonly Mock<ILogger<ZeroBounceClient>> _loggerMock = new();
    private readonly ZeroBounceOptions _options = new()
    {
        ApiKey = "test_key",
        IncludeRawResponse = true
    };

    private ZeroBounceClient CreateClient(MockHttpMessageHandler mockHttp) =>
        new(mockHttp.ToHttpClient(), Options.Create(_options), _loggerMock.Object);

    private ZeroBounceClient CreateClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(_options), _loggerMock.Object);

    // Synchronous on purpose: MockHttp matchers are plain Func<HttpRequestMessage, bool>.
    private static string Body(HttpRequestMessage request)
    {
        using var reader = new StreamReader(request.Content!.ReadAsStream());
        return reader.ReadToEnd();
    }

    #region single validation

    [Fact]
    public async Task ValidateAsync_ValidAddress_MapsEveryField()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(Validate).Respond("application/json", """
            {"address":"someone@example.com","status":"valid","sub_status":"","free_email":false,"catchall_domain":null,"did_you_mean":null,"account":"someone","domain":"example.com","domain_age_days":"9000","smtp_provider":"microsoft","mx_found":"true","mx_record":"example-com.mail.protection.outlook.com","processed_at":"2026-10-06 00:26:48.484"}
            """);

        var result = await CreateClient(mockHttp).ValidateAsync("someone@example.com");

        Assert.Equal("someone@example.com", result.Email);
        Assert.Equal(EmailValidationStatus.Valid, result.Status);
        Assert.Equal(EmailValidationSubStatus.None, result.SubStatus);
        Assert.True(result.IsSafeToSend);
        Assert.False(result.IsConfirmedUndeliverable);
        Assert.False(result.IsInconclusive);
        Assert.Equal("someone", result.Account);
        Assert.Equal("example.com", result.Domain);
        Assert.Null(result.DidYouMean);
        Assert.False(result.IsFreeEmail);
        Assert.True(result.MxFound);
        Assert.Equal("example-com.mail.protection.outlook.com", result.MxRecord);
        Assert.Equal("microsoft", result.SmtpProvider);
        Assert.Null(result.IsCatchAllDomain);
        Assert.Equal(9000, result.DomainAgeDays);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 0, 26, 48, 484, TimeSpan.Zero), result.ProcessedAt);
        Assert.NotNull(result.RawResponse);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_MailboxQuotaExceeded_IsConfirmedUndeliverable()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(Validate).Respond("application/json", QuotaExceededJson);

        var result = await CreateClient(mockHttp).ValidateAsync("full@gmail.com");

        Assert.Equal(EmailValidationStatus.Invalid, result.Status);
        Assert.Equal(EmailValidationSubStatus.MailboxQuotaExceeded, result.SubStatus);
        Assert.True(result.IsConfirmedUndeliverable);
        Assert.True(result.IsMailboxFull);
        Assert.False(result.IsSafeToSend);
        Assert.True(result.IsFreeEmail);
        Assert.False(result.IsCatchAllDomain);
        Assert.Equal("mailbox_quota_exceeded", result.RawSubStatus);
    }

    [Theory]
    [InlineData("invalid", "mailbox_not_found", EmailValidationStatus.Invalid, EmailValidationSubStatus.MailboxNotFound, true)]
    [InlineData("invalid", "failed_syntax_check", EmailValidationStatus.Invalid, EmailValidationSubStatus.FailedSyntaxCheck, true)]
    [InlineData("do_not_mail", "disposable", EmailValidationStatus.DoNotMail, EmailValidationSubStatus.Disposable, true)]
    [InlineData("do_not_mail", "role_based", EmailValidationStatus.DoNotMail, EmailValidationSubStatus.RoleBased, true)]
    [InlineData("do_not_mail", "toxic", EmailValidationStatus.DoNotMail, EmailValidationSubStatus.Toxic, true)]
    [InlineData("spamtrap", "", EmailValidationStatus.SpamTrap, EmailValidationSubStatus.None, true)]
    [InlineData("abuse", "", EmailValidationStatus.Abuse, EmailValidationSubStatus.None, true)]
    [InlineData("catch-all", "", EmailValidationStatus.CatchAll, EmailValidationSubStatus.None, false)]
    [InlineData("unknown", "timeout_exceeded", EmailValidationStatus.Unknown, EmailValidationSubStatus.TimeoutExceeded, false)]
    [InlineData("unknown", "greylisted", EmailValidationStatus.Unknown, EmailValidationSubStatus.Greylisted, false)]
    [InlineData("unknown", "antispam_system", EmailValidationStatus.Unknown, EmailValidationSubStatus.AntispamSystem, false)]
    public async Task ValidateAsync_MapsStatusAndSubStatus(
        string status, string subStatus, EmailValidationStatus expected, EmailValidationSubStatus expectedSub, bool undeliverable)
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(Validate).Respond("application/json",
            $$"""{"address":"a@b.com","status":"{{status}}","sub_status":"{{subStatus}}","mx_found":"false"}""");

        var result = await CreateClient(mockHttp).ValidateAsync("a@b.com");

        Assert.Equal(expected, result.Status);
        Assert.Equal(expectedSub, result.SubStatus);
        Assert.Equal(undeliverable, result.IsConfirmedUndeliverable);
        Assert.Equal(status, result.RawStatus);
    }

    [Fact]
    public async Task ValidateAsync_UnknownSubStatus_FallsBackToOtherAndKeepsRaw()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(Validate).Respond("application/json",
            """{"address":"a@b.com","status":"valid","sub_status":"brand_new_thing","mx_found":true}""");

        var result = await CreateClient(mockHttp).ValidateAsync("a@b.com");

        Assert.Equal(EmailValidationStatus.Valid, result.Status);
        Assert.Equal(EmailValidationSubStatus.Other, result.SubStatus);
        Assert.Equal("brand_new_thing", result.RawSubStatus);
        Assert.True(result.MxFound);
    }

    [Fact]
    public async Task ValidateAsync_UnknownStatus_IsError()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(Validate).Respond("application/json",
            """{"address":"a@b.com","status":"something_new","sub_status":""}""");

        var result = await CreateClient(mockHttp).ValidateAsync("a@b.com");

        Assert.Equal(EmailValidationStatus.Error, result.Status);
        Assert.True(result.IsInconclusive);
    }

    [Fact]
    public async Task ValidateAsync_SendsKeyClampedTimeoutAndIp()
    {
        _options.TimeoutSeconds = 90; // above the API's 60s ceiling
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Get, Validate)
            .WithQueryString("api_key", "test_key")
            .WithQueryString("email", "a@b.com")
            .WithQueryString("timeout", "60")
            .WithQueryString("ip_address", "203.0.113.9")
            .Respond("application/json", """{"address":"a@b.com","status":"valid","sub_status":""}""");

        var result = await CreateClient(mockHttp).ValidateAsync("a@b.com", "203.0.113.9");

        Assert.Equal(EmailValidationStatus.Valid, result.Status);
        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task ValidateAsync_TimeoutBelowFloor_IsClampedUpToThree()
    {
        _options.TimeoutSeconds = 0;
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Get, Validate)
            .WithQueryString("timeout", "3")
            .Respond("application/json", """{"address":"a@b.com","status":"valid","sub_status":""}""");

        await CreateClient(mockHttp).ValidateAsync("a@b.com");

        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task ValidateAsync_OmitsIpWhenNotGiven()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Get, Validate)
            .With(req => !req.RequestUri!.Query.Contains("ip_address"))
            .Respond("application/json", """{"address":"a@b.com","status":"valid","sub_status":""}""");

        await CreateClient(mockHttp).ValidateAsync("a@b.com");

        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ValidateAsync_BlankEmail_IsInvalidWithoutCallingTheApi(string? email)
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Fallback.Throw(new InvalidOperationException("The API must not be called for a blank address."));

        var result = await CreateClient(mockHttp).ValidateAsync(email!);

        Assert.Equal(EmailValidationStatus.Invalid, result.Status);
        Assert.Equal(EmailValidationSubStatus.FailedSyntaxCheck, result.SubStatus);
        Assert.True(result.HasBadSyntax);
        Assert.Equal(email ?? string.Empty, result.Email);
    }

    [Fact]
    public async Task ValidateAsync_ApiErrorBody_IsErrorStatusWithMessage()
    {
        // ZeroBounce returns HTTP 200 for a rejected key / exhausted credits.
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(Validate).Respond("application/json",
            """{"error":"Invalid API key or your account ran out of credits"}""");

        var result = await CreateClient(mockHttp).ValidateAsync("a@b.com");

        Assert.Equal(EmailValidationStatus.Error, result.Status);
        Assert.Equal("Invalid API key or your account ran out of credits", result.ErrorMessage);
        Assert.False(result.IsSafeToSend);
        Assert.False(result.IsConfirmedUndeliverable);
        Assert.True(result.IsInconclusive);
        Assert.Equal("a@b.com", result.Email);
    }

    [Fact]
    public async Task ValidateAsync_ApiErrorBody_ThrowsWhenConfigured()
    {
        _options.ThrowOnError = true;
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(Validate).Respond("application/json", """{"error":"Invalid API key or your account ran out of credits"}""");

        var ex = await Assert.ThrowsAsync<ZeroBounceException>(() => CreateClient(mockHttp).ValidateAsync("a@b.com"));

        Assert.Equal("api_error", ex.Code);
        Assert.Contains("Invalid API key", ex.Message);
    }

    [Fact]
    public async Task ValidateAsync_HttpFailure_IsErrorStatus()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(Validate).Respond(HttpStatusCode.InternalServerError);

        var result = await CreateClient(mockHttp).ValidateAsync("a@b.com");

        Assert.Equal(EmailValidationStatus.Error, result.Status);
        Assert.Contains("500", result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_HttpFailure_ThrowsWhenConfigured()
    {
        _options.ThrowOnError = true;
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(Validate).Respond(HttpStatusCode.BadGateway);

        var ex = await Assert.ThrowsAsync<ZeroBounceException>(() => CreateClient(mockHttp).ValidateAsync("a@b.com"));

        Assert.Equal("http_error", ex.Code);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task ValidateAsync_MalformedJson_IsErrorStatus()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(Validate).Respond("application/json", "<html>not json</html>");

        var result = await CreateClient(mockHttp).ValidateAsync("a@b.com");

        Assert.Equal(EmailValidationStatus.Error, result.Status);
        Assert.Contains("parse", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("<html>not json</html>", result.RawResponse);
    }

    [Fact]
    public async Task ValidateAsync_MalformedJson_ThrowsWhenConfigured()
    {
        _options.ThrowOnError = true;
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(Validate).Respond("application/json", "{");

        var ex = await Assert.ThrowsAsync<ZeroBounceException>(() => CreateClient(mockHttp).ValidateAsync("a@b.com"));

        Assert.Equal("parse_error", ex.Code);
    }

    [Fact]
    public async Task ValidateAsync_CallerCancellation_PropagatesInsteadOfBecomingError()
    {
        var hanging = new HangingHandler();
        using var cts = new CancellationTokenSource();
        var client = CreateClient(hanging);

        var pending = client.ValidateAsync("a@b.com", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ValidateAsync_ServerNeverAnswers_IsErrorAfterBudget()
    {
        // Budget = clamped API timeout (3s floor) + 5s grace, so this test takes ~8s.
        _options.TimeoutSeconds = 3;
        var hanging = new HangingHandler();

        var result = await CreateClient(hanging).ValidateAsync("a@b.com");

        Assert.Equal(EmailValidationStatus.Error, result.Status);
        Assert.Contains("timed out", result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_RawResponseOmittedByDefault()
    {
        _options.IncludeRawResponse = false;
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(Validate).Respond("application/json", QuotaExceededJson);

        var result = await CreateClient(mockHttp).ValidateAsync("full@gmail.com");

        Assert.Null(result.RawResponse);
    }

    [Fact]
    public async Task IsSafeToSendAsync_ReflectsVerdict()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(Validate).WithQueryString("email", "ok@b.com")
            .Respond("application/json", """{"address":"ok@b.com","status":"catch-all","sub_status":""}""");
        mockHttp.When(Validate).WithQueryString("email", "dead@b.com")
            .Respond("application/json", """{"address":"dead@b.com","status":"invalid","sub_status":"mailbox_not_found"}""");
        var client = CreateClient(mockHttp);

        Assert.True(await client.IsSafeToSendAsync("ok@b.com"));
        Assert.False(await client.IsSafeToSendAsync("dead@b.com"));
    }

    #endregion

    #region batch validation

    [Fact]
    public async Task ValidateBatchAsync_PostsJsonBodyWithKeyAddressesAndTimeout()
    {
        _options.BatchTimeoutSeconds = 500; // above the 120s ceiling
        string? sentBody = null;
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Post, ValidateBatch)
            .With(req => { sentBody = Body(req); return true; })
            .Respond("application/json", """{"email_batch":[],"errors":[]}""");

        await CreateClient(mockHttp).ValidateBatchAsync(["one@b.com", "two@b.com"]);

        mockHttp.VerifyNoOutstandingExpectation();
        Assert.NotNull(sentBody);
        Assert.Contains("\"api_key\":\"test_key\"", sentBody);
        Assert.Contains("\"email_address\":\"one@b.com\"", sentBody);
        Assert.Contains("\"email_address\":\"two@b.com\"", sentBody);
        Assert.Contains("\"timeout\":120", sentBody);
        Assert.DoesNotContain("ip_address", sentBody);
    }

    [Fact]
    public async Task ValidateBatchAsync_MapsResultsInRequestOrderAndFillsGaps()
    {
        var mockHttp = new MockHttpMessageHandler();
        // Answer out of order and leave "missing@b.com" out entirely.
        mockHttp.When(HttpMethod.Post, ValidateBatch).Respond("application/json", """
            {"email_batch":[
              {"address":"dead@tidewise.io","status":"invalid","sub_status":"mailbox_not_found","free_email":false,"catchall_domain":false,"mx_found":"true","smtp_provider":"g-suite","domain_age_days":"2785"},
              {"address":"temp@emailinbo.live","status":"invalid","sub_status":"mailbox_quota_exceeded","free_email":true,"catchall_domain":false,"mx_found":"true","domain_age_days":""},
              {"address":"not-an-email","status":"invalid","sub_status":"failed_syntax_check","free_email":false,"catchall_domain":null,"mx_found":"false","mx_record":null}
            ],"errors":[]}
            """);

        var batch = await CreateClient(mockHttp).ValidateBatchAsync(
            ["temp@emailinbo.live", "missing@b.com", "dead@tidewise.io", "not-an-email"]);

        Assert.False(batch.HasErrors);
        Assert.Equal(4, batch.Results.Count);
        Assert.Equal(["temp@emailinbo.live", "missing@b.com", "dead@tidewise.io", "not-an-email"], batch.Results.Select(r => r.Email));

        Assert.True(batch.Results[0].IsMailboxFull);
        Assert.Null(batch.Results[0].DomainAgeDays);

        Assert.Equal(EmailValidationStatus.Error, batch.Results[1].Status);
        Assert.Contains("no verdict", batch.Results[1].ErrorMessage);

        Assert.True(batch.Results[2].IsMailboxNotFound);
        Assert.Equal(2785, batch.Results[2].DomainAgeDays);
        Assert.Equal("g-suite", batch.Results[2].SmtpProvider);

        Assert.True(batch.Results[3].HasBadSyntax);
        Assert.Null(batch.Results[3].IsCatchAllDomain);
        Assert.False(batch.Results[3].MxFound);
    }

    [Fact]
    public async Task ValidateBatchAsync_AccountLevelError_MarksEveryAddressAsError()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, ValidateBatch).Respond("application/json",
            """{"errors":[{"error":"Invalid API Key or your account ran out of credits","email_address":"all"}],"email_batch":[]}""");

        var batch = await CreateClient(mockHttp).ValidateBatchAsync(["one@b.com", "two@b.com"]);

        Assert.True(batch.HasErrors);
        var error = Assert.Single(batch.Errors);
        Assert.True(error.AppliesToAll);
        Assert.Equal("Invalid API Key or your account ran out of credits", error.Message);
        Assert.Equal(2, batch.Results.Count);
        Assert.All(batch.Results, r =>
        {
            Assert.Equal(EmailValidationStatus.Error, r.Status);
            Assert.Equal(error.Message, r.ErrorMessage);
        });
    }

    [Fact]
    public async Task ValidateBatchAsync_AccountLevelError_ThrowsWhenConfigured()
    {
        _options.ThrowOnError = true;
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, ValidateBatch).Respond("application/json",
            """{"errors":[{"error":"Invalid API Key or your account ran out of credits","email_address":"all"}],"email_batch":[]}""");

        var ex = await Assert.ThrowsAsync<ZeroBounceException>(() => CreateClient(mockHttp).ValidateBatchAsync(["one@b.com"]));

        Assert.Equal("api_error", ex.Code);
    }

    [Fact]
    public async Task ValidateBatchAsync_PerAddressError_IsReportedWithoutFailingTheBatch()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, ValidateBatch).Respond("application/json", """
            {"email_batch":[{"address":"ok@b.com","status":"valid","sub_status":""}],
             "errors":[{"error":"Something specific","email_address":"odd@b.com"}]}
            """);

        var batch = await CreateClient(mockHttp).ValidateBatchAsync(["ok@b.com", "odd@b.com"]);

        var error = Assert.Single(batch.Errors);
        Assert.False(error.AppliesToAll);
        Assert.Equal("odd@b.com", error.EmailAddress);
        Assert.Equal(EmailValidationStatus.Valid, batch.Results[0].Status);
        Assert.Equal(EmailValidationStatus.Error, batch.Results[1].Status);
    }

    [Fact]
    public async Task ValidateBatchAsync_HttpFailure_MarksChunkAsErrorWithoutThrowing()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, ValidateBatch).Respond(HttpStatusCode.ServiceUnavailable);

        var batch = await CreateClient(mockHttp).ValidateBatchAsync(["one@b.com", "two@b.com"]);

        Assert.True(batch.HasErrors);
        Assert.True(batch.Errors[0].AppliesToAll);
        Assert.Equal(2, batch.Results.Count);
        Assert.All(batch.Results, r => Assert.Equal(EmailValidationStatus.Error, r.Status));
    }

    [Fact]
    public async Task ValidateBatchAsync_ChunksAtTwoHundred()
    {
        var mockHttp = new MockHttpMessageHandler();
        var sizes = new List<int>();
        mockHttp.When(HttpMethod.Post, ValidateBatch)
            .With(req =>
            {
                var body = Body(req);
                sizes.Add(body.Split("\"email_address\"").Length - 1);
                return true;
            })
            .Respond("application/json", """{"email_batch":[],"errors":[]}""");

        var emails = Enumerable.Range(1, 450).Select(i => $"user{i}@b.com").ToList();
        var batch = await CreateClient(mockHttp).ValidateBatchAsync(emails);

        Assert.Equal(3, sizes.Count);
        Assert.Equal([200, 200, 50], sizes);
        Assert.Equal(450, batch.Results.Count);
    }

    [Fact]
    public async Task ValidateBatchAsync_SkipsBlankEntriesAndTrims()
    {
        string? sentBody = null;
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, ValidateBatch)
            .With(req => { sentBody = Body(req); return true; })
            .Respond("application/json", """{"email_batch":[{"address":"one@b.com","status":"valid","sub_status":""}],"errors":[]}""");

        var batch = await CreateClient(mockHttp).ValidateBatchAsync(["  one@b.com ", "", "   ", null!]);

        Assert.Single(batch.Results);
        Assert.Equal("one@b.com", batch.Results[0].Email);
        Assert.Equal(EmailValidationStatus.Valid, batch.Results[0].Status);
        Assert.Equal(1, sentBody!.Split("\"email_address\"").Length - 1);
    }

    [Fact]
    public async Task ValidateBatchAsync_EmptyInput_MakesNoRequest()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Fallback.Throw(new InvalidOperationException("No request expected."));

        var batch = await CreateClient(mockHttp).ValidateBatchAsync([]);

        Assert.Empty(batch.Results);
        Assert.False(batch.HasErrors);
    }

    #endregion

    #region credits

    [Fact]
    public async Task GetCreditsAsync_ParsesStringBalance()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(HttpMethod.Get, GetCredits)
            .WithQueryString("api_key", "test_key")
            .Respond("application/json", """{"Credits":"5100"}""");

        var credits = await CreateClient(mockHttp).GetCreditsAsync();

        Assert.Equal(5100, credits);
        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task GetCreditsAsync_MinusOne_MeansRejectedKey()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(GetCredits).Respond("application/json", """{"Credits":"-1"}""");

        var ex = await Assert.ThrowsAsync<ZeroBounceException>(() => CreateClient(mockHttp).GetCreditsAsync());

        Assert.Equal("invalid_api_key", ex.Code);
    }

    [Fact]
    public async Task GetCreditsAsync_HttpFailure_AlwaysThrows()
    {
        _options.ThrowOnError = false;
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(GetCredits).Respond(HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<ZeroBounceException>(() => CreateClient(mockHttp).GetCreditsAsync());

        Assert.Equal("request_failed", ex.Code);
    }

    [Fact]
    public async Task GetCreditsAsync_UnreadableBalance_Throws()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(GetCredits).Respond("application/json", """{"Credits":"lots"}""");

        var ex = await Assert.ThrowsAsync<ZeroBounceException>(() => CreateClient(mockHttp).GetCreditsAsync());

        Assert.Equal("parse_error", ex.Code);
    }

    #endregion

    #region construction

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankApiKey_Throws(string key)
    {
        _options.ApiKey = key;

        Assert.Throws<ArgumentException>(() => CreateClient(new MockHttpMessageHandler()));
    }

    [Fact]
    public void Constructor_UsesConfiguredBaseUrl()
    {
        _options.BaseUrl = ZeroBounceOptions.EuBaseUrl;
        var mockHttp = new MockHttpMessageHandler();

        var client = mockHttp.ToHttpClient();
        _ = new ZeroBounceClient(client, Options.Create(_options), _loggerMock.Object);

        Assert.Equal(new Uri("https://api-eu.zerobounce.net/v2/"), client.BaseAddress);
    }

    #endregion
}

/// <summary>
/// Never answers. Completes only when the request token is cancelled, which is
/// exactly what a mail server that has stopped talking looks like to HttpClient.
/// </summary>
internal sealed class HangingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
