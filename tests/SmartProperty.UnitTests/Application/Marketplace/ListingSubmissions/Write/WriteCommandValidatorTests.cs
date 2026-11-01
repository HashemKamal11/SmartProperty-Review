using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Create;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Discard;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Update;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Request validation for the four write commands: only rules the Domain already enforces, always the same
/// validation error shape, and a draft may have no evidence yet.
/// </summary>
public sealed class WriteCommandValidatorTests
{
    private static readonly Guid PropertyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SubmissionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ----- create

    [Fact]
    public void AValidSaleDraftPasses()
    {
        Assert.Null(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content())));
    }

    [Theory]
    [InlineData(RentPeriod.Monthly)]
    [InlineData(RentPeriod.Yearly)]
    public void AValidRentalDraftPasses(RentPeriod period)
    {
        Assert.Null(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Rent, Content(rentPeriod: period))));
    }

    [Fact]
    public void ADraftMayHaveNoEvidenceYet()
    {
        Assert.Null(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content(evidence: []))));
    }

    [Fact]
    public void ADraftWithEvidenceIdsPasses()
    {
        Assert.Null(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content(evidence: [Guid.NewGuid(), Guid.NewGuid()]))));
    }

    [Fact]
    public void ACreateWithoutAPropertyOrWithAnUndefinedPurposeIsRefused()
    {
        AssertValidation(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content()) with { PropertyId = Guid.Empty }));
        AssertValidation(CreateListingDraftCommandValidator.Validate(Create((ListingPurpose)99, Content())));
        AssertValidation(CreateListingDraftCommandValidator.Validate(Create((ListingPurpose)0, Content())));
    }

    [Fact]
    public void ARentalNeedsARentPeriodAndASaleMustNotHaveOne()
    {
        AssertValidation(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Rent, Content(rentPeriod: null))));
        AssertValidation(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Rent, Content(rentPeriod: (RentPeriod)99))));
        AssertValidation(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content(rentPeriod: RentPeriod.Monthly))));
        AssertValidation(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content(rentPeriod: (RentPeriod)99))));
    }

    [Fact]
    public void MissingContentIsRefused()
    {
        AssertValidation(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, null!)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankTitleDescriptionOrCurrencyIsRefused(string? text)
    {
        AssertValidation(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content(title: text!))));
        AssertValidation(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content(description: text!))));
        AssertValidation(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content(currency: text!))));
    }

    [Fact]
    public void EvidenceIdsMustBePresentNonEmptyAndUnique()
    {
        var repeated = Guid.NewGuid();

        AssertValidation(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, NullEvidenceContent())));
        AssertValidation(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content(evidence: [Guid.Empty]))));
        AssertValidation(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content(evidence: [repeated, repeated]))));
    }

    [Fact]
    public void NoLimitIsInventedWhereTheDomainSetsNone()
    {
        Assert.Null(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content(amount: 0m))));
        Assert.Null(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content(amount: decimal.MaxValue))));
        Assert.Null(CreateListingDraftCommandValidator.Validate(Create(ListingPurpose.Sale, Content(title: new string('a', 10_000)))));
    }

    // ----- update

    [Fact]
    public void AValidUpdatePassesWithOrWithoutARentPeriod()
    {
        Assert.Null(UpdateListingDraftCommandValidator.Validate(new UpdateListingDraftCommand(SubmissionId, 0, Content())));
        Assert.Null(UpdateListingDraftCommandValidator.Validate(new UpdateListingDraftCommand(SubmissionId, 3, Content(rentPeriod: RentPeriod.Yearly))));
    }

    [Fact]
    public void AnUpdateNeedsASubmissionAndANonNegativeRevision()
    {
        AssertValidation(UpdateListingDraftCommandValidator.Validate(new UpdateListingDraftCommand(Guid.Empty, 0, Content())));
        AssertValidation(UpdateListingDraftCommandValidator.Validate(new UpdateListingDraftCommand(SubmissionId, -1, Content())));
    }

    [Fact]
    public void AnUpdateChecksTheContentButLeavesThePurposeRuleToTheHandler()
    {
        AssertValidation(UpdateListingDraftCommandValidator.Validate(new UpdateListingDraftCommand(SubmissionId, 0, null!)));
        AssertValidation(UpdateListingDraftCommandValidator.Validate(new UpdateListingDraftCommand(SubmissionId, 0, Content(title: " "))));
        AssertValidation(UpdateListingDraftCommandValidator.Validate(new UpdateListingDraftCommand(SubmissionId, 0, Content(rentPeriod: (RentPeriod)99))));
        AssertValidation(UpdateListingDraftCommandValidator.Validate(new UpdateListingDraftCommand(SubmissionId, 0, Content(evidence: [Guid.Empty]))));
    }

    // ----- discard and submit

    [Fact]
    public void AValidDiscardAndSubmitPass()
    {
        Assert.Null(DiscardListingDraftCommandValidator.Validate(new DiscardListingDraftCommand(SubmissionId, 0)));
        Assert.Null(SubmitListingDraftCommandValidator.Validate(new SubmitListingDraftCommand(SubmissionId, 5)));
    }

    [Fact]
    public void DiscardAndSubmitNeedASubmissionAndANonNegativeRevision()
    {
        AssertValidation(DiscardListingDraftCommandValidator.Validate(new DiscardListingDraftCommand(Guid.Empty, 0)));
        AssertValidation(DiscardListingDraftCommandValidator.Validate(new DiscardListingDraftCommand(SubmissionId, -1)));
        AssertValidation(SubmitListingDraftCommandValidator.Validate(new SubmitListingDraftCommand(Guid.Empty, 0)));
        AssertValidation(SubmitListingDraftCommandValidator.Validate(new SubmitListingDraftCommand(SubmissionId, -1)));
    }

    [Fact]
    public void ANullCommandIsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => CreateListingDraftCommandValidator.Validate(null!));
        Assert.Throws<ArgumentNullException>(() => UpdateListingDraftCommandValidator.Validate(null!));
        Assert.Throws<ArgumentNullException>(() => DiscardListingDraftCommandValidator.Validate(null!));
        Assert.Throws<ArgumentNullException>(() => SubmitListingDraftCommandValidator.Validate(null!));
    }

    // ----- errors

    [Fact]
    public void TheWriteErrorsAreStableAndGeneric()
    {
        Assert.Equal("listing_submissions.not_eligible", WriteErrors.NotEligible.Code);
        Assert.Equal(ErrorType.Forbidden, WriteErrors.NotEligible.Type);
        Assert.Equal("listing_submissions.open_revision_exists", WriteErrors.OpenRevisionExists.Code);
        Assert.Equal(ErrorType.Conflict, WriteErrors.OpenRevisionExists.Type);
        Assert.Equal("listing_submissions.stale_revision", WriteErrors.StaleRevision.Code);
        Assert.Equal(ErrorType.Conflict, WriteErrors.StaleRevision.Type);
        Assert.Equal("listing_submissions.prerequisites_unverified", WriteErrors.PrerequisitesUnverified.Code);
        Assert.Equal(ErrorType.Conflict, WriteErrors.PrerequisitesUnverified.Type);

        var errors = new[] { WriteErrors.NotEligible, WriteErrors.OpenRevisionExists, WriteErrors.StaleRevision, WriteErrors.PrerequisitesUnverified };
        Assert.Equal(errors.Length, errors.Select(error => error.Code).Distinct().Count());

        // None says which condition failed, who holds what, or anything about a grant, a party or a property id.
        foreach (var error in errors)
        {
            foreach (var word in new[] { "grant", "issuer", "reviewer", "revoked", "expired", "legal", "owner", "scan", "digest", "workspace" })
            {
                Assert.DoesNotContain(word, error.Description, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ----- helpers

    private static void AssertValidation(Error? error)
    {
        Assert.NotNull(error);
        Assert.Equal("validation.failed", error.Code);
        Assert.Equal(ErrorType.Validation, error.Type);
    }

    private static CreateListingDraftCommand Create(ListingPurpose purpose, ListingDraftContentInput content)
    {
        return new CreateListingDraftCommand(PropertyId, purpose, content);
    }

    private static ListingDraftContentInput Content(
        string title = "Two bedroom apartment",
        string description = "Close to the centre.",
        decimal amount = 1250000m,
        string currency = "SAR",
        RentPeriod? rentPeriod = null,
        IReadOnlyList<Guid>? evidence = null)
    {
        return new ListingDraftContentInput(title, description, amount, currency, rentPeriod, evidence ?? []);
    }

    private static ListingDraftContentInput NullEvidenceContent()
    {
        return new ListingDraftContentInput("Title", "Description", 1m, "SAR", null, null!);
    }
}
