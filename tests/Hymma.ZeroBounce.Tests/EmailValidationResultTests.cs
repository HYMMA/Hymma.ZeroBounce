namespace Hymma.ZeroBounce.Tests;

public class EmailValidationResultTests
{
    private static EmailValidationResult Result(EmailValidationStatus status, EmailValidationSubStatus sub = EmailValidationSubStatus.None) =>
        new() { Email = "a@b.com", Status = status, SubStatus = sub };

    [Theory]
    [InlineData(EmailValidationStatus.Valid, true, false, false)]
    [InlineData(EmailValidationStatus.CatchAll, true, false, false)]
    [InlineData(EmailValidationStatus.Invalid, false, true, false)]
    [InlineData(EmailValidationStatus.SpamTrap, false, true, false)]
    [InlineData(EmailValidationStatus.Abuse, false, true, false)]
    [InlineData(EmailValidationStatus.DoNotMail, false, true, false)]
    [InlineData(EmailValidationStatus.Unknown, false, false, true)]
    [InlineData(EmailValidationStatus.Error, false, false, true)]
    public void StatusPartitionsIntoSafeUndeliverableAndInconclusive(
        EmailValidationStatus status, bool safe, bool undeliverable, bool inconclusive)
    {
        var result = Result(status);

        Assert.Equal(safe, result.IsSafeToSend);
        Assert.Equal(undeliverable, result.IsConfirmedUndeliverable);
        Assert.Equal(inconclusive, result.IsInconclusive);
    }

    [Fact]
    public void EveryStatusFallsInExactlyOnePartition()
    {
        foreach (var status in Enum.GetValues<EmailValidationStatus>())
        {
            var r = Result(status);
            var buckets = new[] { r.IsSafeToSend, r.IsConfirmedUndeliverable, r.IsInconclusive }.Count(b => b);
            Assert.True(buckets == 1, $"{status} is in {buckets} partitions");
        }
    }

    [Theory]
    [InlineData(EmailValidationSubStatus.Disposable, nameof(EmailValidationResult.IsDisposable))]
    [InlineData(EmailValidationSubStatus.Toxic, nameof(EmailValidationResult.IsToxic))]
    [InlineData(EmailValidationSubStatus.RoleBased, nameof(EmailValidationResult.IsRoleBased))]
    [InlineData(EmailValidationSubStatus.RoleBasedCatchAll, nameof(EmailValidationResult.IsRoleBased))]
    [InlineData(EmailValidationSubStatus.MailboxQuotaExceeded, nameof(EmailValidationResult.IsMailboxFull))]
    [InlineData(EmailValidationSubStatus.MailboxNotFound, nameof(EmailValidationResult.IsMailboxNotFound))]
    [InlineData(EmailValidationSubStatus.FailedSyntaxCheck, nameof(EmailValidationResult.HasBadSyntax))]
    [InlineData(EmailValidationSubStatus.TimeoutExceeded, nameof(EmailValidationResult.TimedOut))]
    public void SubStatusFlags_ReflectSubStatus(EmailValidationSubStatus sub, string flag)
    {
        var flagged = Result(EmailValidationStatus.Invalid, sub);
        var plain = Result(EmailValidationStatus.Invalid);

        var property = typeof(EmailValidationResult).GetProperty(flag)!;
        Assert.True((bool)property.GetValue(flagged)!);
        Assert.False((bool)property.GetValue(plain)!);
    }

    [Fact]
    public void Defaults_AreNullOrFalse()
    {
        var result = new EmailValidationResult { Email = "a@b.com" };

        Assert.Equal(EmailValidationStatus.Valid, result.Status); // enum default
        Assert.Equal(EmailValidationSubStatus.None, result.SubStatus);
        Assert.Null(result.DidYouMean);
        Assert.Null(result.IsCatchAllDomain);
        Assert.Null(result.DomainAgeDays);
        Assert.Null(result.ProcessedAt);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.RawResponse);
        Assert.False(result.IsFreeEmail);
        Assert.False(result.MxFound);
    }
}
