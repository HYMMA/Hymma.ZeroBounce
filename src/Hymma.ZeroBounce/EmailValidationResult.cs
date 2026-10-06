namespace Hymma.ZeroBounce;

/// <summary>
/// Result of validating one email address with ZeroBounce.
/// </summary>
public class EmailValidationResult
{
    /// <summary>
    /// The email address that was validated.
    /// </summary>
    public required string Email { get; init; }

    /// <summary>
    /// The primary verdict.
    /// </summary>
    public EmailValidationStatus Status { get; init; }

    /// <summary>
    /// The reason behind the verdict, when ZeroBounce gives one.
    /// </summary>
    public EmailValidationSubStatus SubStatus { get; init; } = EmailValidationSubStatus.None;

    /// <summary>
    /// The <c>status</c> string exactly as ZeroBounce returned it.
    /// </summary>
    public string? RawStatus { get; init; }

    /// <summary>
    /// The <c>sub_status</c> string exactly as ZeroBounce returned it. Useful when
    /// <see cref="SubStatus"/> is <see cref="EmailValidationSubStatus.Other"/>.
    /// </summary>
    public string? RawSubStatus { get; init; }

    /// <summary>
    /// The local part of the address (before the @).
    /// </summary>
    public string? Account { get; init; }

    /// <summary>
    /// The domain part of the address (after the @).
    /// </summary>
    public string? Domain { get; init; }

    /// <summary>
    /// A suggested correction when the address looks like a typo
    /// (e.g. <c>gmial.com</c> → <c>gmail.com</c>).
    /// </summary>
    public string? DidYouMean { get; init; }

    /// <summary>
    /// Whether the domain is a free email provider (Gmail, Yahoo, …).
    /// </summary>
    public bool IsFreeEmail { get; init; }

    /// <summary>
    /// Whether the domain has an MX record.
    /// </summary>
    public bool MxFound { get; init; }

    /// <summary>
    /// The preferred MX record of the domain.
    /// </summary>
    public string? MxRecord { get; init; }

    /// <summary>
    /// The SMTP provider ZeroBounce detected (e.g. <c>google</c>, <c>g-suite</c>, <c>microsoft</c>).
    /// </summary>
    public string? SmtpProvider { get; init; }

    /// <summary>
    /// Whether the domain accepts mail for any local part. Null when ZeroBounce
    /// could not tell.
    /// </summary>
    public bool? IsCatchAllDomain { get; init; }

    /// <summary>
    /// Age of the domain in days, when known.
    /// </summary>
    public int? DomainAgeDays { get; init; }

    /// <summary>
    /// When ZeroBounce processed the request (UTC).
    /// </summary>
    public DateTimeOffset? ProcessedAt { get; init; }

    /// <summary>
    /// The failure message when <see cref="Status"/> is <see cref="EmailValidationStatus.Error"/>.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Raw JSON response from ZeroBounce, when <see cref="ZeroBounceOptions.IncludeRawResponse"/>
    /// is on (single validation only).
    /// </summary>
    public string? RawResponse { get; init; }

    /// <summary>
    /// True when the address is a reasonable send target:
    /// <see cref="EmailValidationStatus.Valid"/> or <see cref="EmailValidationStatus.CatchAll"/>.
    /// </summary>
    public bool IsSafeToSend =>
        Status is EmailValidationStatus.Valid or EmailValidationStatus.CatchAll;

    /// <summary>
    /// True when ZeroBounce is confident that sending would bounce or hurt sender
    /// reputation: <see cref="EmailValidationStatus.Invalid"/>,
    /// <see cref="EmailValidationStatus.SpamTrap"/>, <see cref="EmailValidationStatus.Abuse"/>
    /// or <see cref="EmailValidationStatus.DoNotMail"/>. This is the verdict to block on;
    /// an inconclusive answer (<see cref="IsInconclusive"/>) is not a reason to block.
    /// </summary>
    public bool IsConfirmedUndeliverable =>
        Status is EmailValidationStatus.Invalid
               or EmailValidationStatus.SpamTrap
               or EmailValidationStatus.Abuse
               or EmailValidationStatus.DoNotMail;

    /// <summary>
    /// True when no verdict was reached: ZeroBounce answered
    /// <see cref="EmailValidationStatus.Unknown"/>, or the request itself failed
    /// (<see cref="EmailValidationStatus.Error"/>).
    /// </summary>
    public bool IsInconclusive =>
        Status is EmailValidationStatus.Unknown or EmailValidationStatus.Error;

    /// <summary>
    /// Whether the address is a temporary/disposable mailbox.
    /// </summary>
    public bool IsDisposable => SubStatus == EmailValidationSubStatus.Disposable;

    /// <summary>
    /// Whether the address belongs to a known complainer / litigator list.
    /// </summary>
    public bool IsToxic => SubStatus == EmailValidationSubStatus.Toxic;

    /// <summary>
    /// Whether the address is a role account (info@, support@, sales@, …).
    /// </summary>
    public bool IsRoleBased =>
        SubStatus is EmailValidationSubStatus.RoleBased or EmailValidationSubStatus.RoleBasedCatchAll;

    /// <summary>
    /// Whether the mailbox exists but is over quota — mail to it soft-bounces.
    /// </summary>
    public bool IsMailboxFull => SubStatus == EmailValidationSubStatus.MailboxQuotaExceeded;

    /// <summary>
    /// Whether the mailbox does not exist on an otherwise working domain.
    /// </summary>
    public bool IsMailboxNotFound => SubStatus == EmailValidationSubStatus.MailboxNotFound;

    /// <summary>
    /// Whether the address is not syntactically an email address.
    /// </summary>
    public bool HasBadSyntax => SubStatus == EmailValidationSubStatus.FailedSyntaxCheck;

    /// <summary>
    /// Whether ZeroBounce ran out of its <see cref="ZeroBounceOptions.TimeoutSeconds"/>
    /// budget before the mail server answered.
    /// </summary>
    public bool TimedOut => SubStatus == EmailValidationSubStatus.TimeoutExceeded;
}

/// <summary>
/// Primary validation status from ZeroBounce.
/// </summary>
public enum EmailValidationStatus
{
    /// <summary>Verified as a real, deliverable mailbox.</summary>
    Valid,

    /// <summary>Verified as undeliverable — will hard bounce (or soft bounce when the mailbox is full).</summary>
    Invalid,

    /// <summary>The domain accepts mail for any local part, so the mailbox cannot be verified.</summary>
    CatchAll,

    /// <summary>ZeroBounce could not reach a verdict (server did not respond, greylisting, timeout, …).</summary>
    Unknown,

    /// <summary>A known spam trap. Never send.</summary>
    SpamTrap,

    /// <summary>A known complainer who marks mail as spam. Never send.</summary>
    Abuse,

    /// <summary>ZeroBounce advises against sending: disposable, toxic, role-based or globally suppressed.</summary>
    DoNotMail,

    /// <summary>The validation request itself failed (network, timeout, bad response, rejected key).</summary>
    Error
}

/// <summary>
/// Secondary status from ZeroBounce explaining the primary verdict.
/// </summary>
public enum EmailValidationSubStatus
{
    /// <summary>No sub-status returned.</summary>
    None,

    /// <summary>The address is an alternate address for a mailbox.</summary>
    Alternate,

    /// <summary>An anti-spam system blocked the verification.</summary>
    AntispamSystem,

    /// <summary>The mail server greylisted the verification attempt.</summary>
    Greylisted,

    /// <summary>The mail server returned a temporary error.</summary>
    MailServerTemporaryError,

    /// <summary>The mail server dropped the connection.</summary>
    ForcibleDisconnect,

    /// <summary>The mail server did not respond.</summary>
    MailServerDidNotRespond,

    /// <summary>Verification exceeded the requested timeout.</summary>
    TimeoutExceeded,

    /// <summary>Could not connect to the mail server.</summary>
    FailedSmtpConnection,

    /// <summary>The mailbox is over quota.</summary>
    MailboxQuotaExceeded,

    /// <summary>An unexpected error occurred during verification.</summary>
    ExceptionOccurred,

    /// <summary>The address may be a spam trap.</summary>
    PossibleTrap,

    /// <summary>A role account such as info@ or support@.</summary>
    RoleBased,

    /// <summary>The address is on a global suppression list.</summary>
    GlobalSuppression,

    /// <summary>The mailbox does not exist.</summary>
    MailboxNotFound,

    /// <summary>The domain has no DNS entries.</summary>
    NoDnsEntries,

    /// <summary>The address is not syntactically valid.</summary>
    FailedSyntaxCheck,

    /// <summary>The address looks like a typo of a common domain.</summary>
    PossibleTypo,

    /// <summary>The mail server's IP address is not routable.</summary>
    UnroutableIpAddress,

    /// <summary>A leading period was removed from the local part.</summary>
    LeadingPeriodRemoved,

    /// <summary>The domain does not accept mail.</summary>
    DoesNotAcceptMail,

    /// <summary>The address is an alias.</summary>
    AliasAddress,

    /// <summary>A role account on a catch-all domain.</summary>
    RoleBasedCatchAll,

    /// <summary>A temporary / disposable mailbox.</summary>
    Disposable,

    /// <summary>A known complainer or litigator.</summary>
    Toxic,

    /// <summary>The domain accepts all mail.</summary>
    AcceptAll,

    /// <summary>Verified with ZeroBounce's highest-confidence method.</summary>
    Gold,

    /// <summary>A sub-status this library does not know; see <see cref="EmailValidationResult.RawSubStatus"/>.</summary>
    Other
}
