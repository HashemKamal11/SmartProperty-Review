using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Queue;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Review;

public sealed class GetReviewQueueQueryValidatorTests
{
    [Fact]
    public void NoFiltersIsValid()
    {
        Assert.Null(GetReviewQueueQueryValidator.Validate(new GetReviewQueueQuery(null, null, null, null)));
    }

    [Theory]
    [InlineData(ListingSubmissionStatus.PendingReview)]
    [InlineData(ListingSubmissionStatus.Approved)]
    [InlineData(ListingSubmissionStatus.Rejected)]
    [InlineData(ListingSubmissionStatus.Superseded)]
    public void EveryReviewableStatusIsValid(ListingSubmissionStatus status)
    {
        Assert.Null(GetReviewQueueQueryValidator.Validate(new GetReviewQueueQuery(status, null, null, null)));
    }

    [Fact]
    public void ADraftIsNotReviewable()
    {
        var error = GetReviewQueueQueryValidator.Validate(new GetReviewQueueQuery(ListingSubmissionStatus.Draft, null, null, null));

        AssertValidationFailure(error);
        Assert.Contains("Draft", error!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUndefinedStatusIsRejected()
    {
        AssertValidationFailure(GetReviewQueueQueryValidator.Validate(new GetReviewQueueQuery((ListingSubmissionStatus)99, null, null, null)));
    }

    [Theory]
    [InlineData(ListingPurpose.Sale)]
    [InlineData(ListingPurpose.Rent)]
    public void EachDefinedPurposeIsValid(ListingPurpose purpose)
    {
        Assert.Null(GetReviewQueueQueryValidator.Validate(new GetReviewQueueQuery(null, purpose, null, null)));
    }

    [Fact]
    public void AnUndefinedPurposeIsRejected()
    {
        AssertValidationFailure(GetReviewQueueQueryValidator.Validate(new GetReviewQueueQuery(null, (ListingPurpose)99, null, null)));
    }

    [Fact]
    public void ANullQueryIsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => GetReviewQueueQueryValidator.Validate(null!));
    }

    [Fact]
    public void TheReviewErrorsAreTheStableCodes()
    {
        Assert.Equal("authorization.forbidden", ReviewErrors.Forbidden.Code);
        Assert.Equal(ErrorType.Forbidden, ReviewErrors.Forbidden.Type);
        Assert.Equal("listing_submissions.reviewer_not_independent", ReviewErrors.NotIndependent.Code);
        Assert.Equal(ErrorType.Forbidden, ReviewErrors.NotIndependent.Type);
        Assert.Equal("listing_submissions.independence_unverifiable", ReviewErrors.IndependenceUnverifiable.Code);
        Assert.Equal(ErrorType.Forbidden, ReviewErrors.IndependenceUnverifiable.Type);
    }

    private static void AssertValidationFailure(Error? error)
    {
        Assert.NotNull(error);
        Assert.Equal("validation.failed", error.Code);
        Assert.Equal(ErrorType.Validation, error.Type);
    }
}
