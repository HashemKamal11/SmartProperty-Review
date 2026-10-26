using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.Mine;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions;

public sealed class GetMyListingSubmissionsQueryValidatorTests
{
    [Fact]
    public void NoFiltersIsValid()
    {
        Assert.Null(GetMyListingSubmissionsQueryValidator.Validate(new GetMyListingSubmissionsQuery(null, null, null, null)));
    }

    [Theory]
    [InlineData(ListingPurpose.Sale)]
    [InlineData(ListingPurpose.Rent)]
    public void EachDefinedPurposeIsValid(ListingPurpose purpose)
    {
        Assert.Null(GetMyListingSubmissionsQueryValidator.Validate(new GetMyListingSubmissionsQuery(purpose, null, null, null)));
    }

    [Theory]
    [InlineData(ListingSubmissionStatus.Draft)]
    [InlineData(ListingSubmissionStatus.PendingReview)]
    [InlineData(ListingSubmissionStatus.Approved)]
    [InlineData(ListingSubmissionStatus.Rejected)]
    [InlineData(ListingSubmissionStatus.Superseded)]
    public void EachDefinedStatusIsValid(ListingSubmissionStatus status)
    {
        Assert.Null(GetMyListingSubmissionsQueryValidator.Validate(new GetMyListingSubmissionsQuery(null, status, null, null)));
    }

    [Fact]
    public void AnUndefinedPurposeIsRejected()
    {
        AssertValidationFailure(GetMyListingSubmissionsQueryValidator.Validate(new GetMyListingSubmissionsQuery((ListingPurpose)99, null, null, null)));
    }

    [Fact]
    public void AnUndefinedStatusIsRejected()
    {
        AssertValidationFailure(GetMyListingSubmissionsQueryValidator.Validate(new GetMyListingSubmissionsQuery(null, (ListingSubmissionStatus)99, null, null)));
    }

    [Fact]
    public void ANullQueryIsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => GetMyListingSubmissionsQueryValidator.Validate(null!));
    }

    [Fact]
    public void TheNotFoundAndUnauthorizedErrorsAreTheStableCodes()
    {
        Assert.Equal("listing_submissions.not_found", ListingSubmissionErrors.NotFound.Code);
        Assert.Equal(ErrorType.NotFound, ListingSubmissionErrors.NotFound.Type);
        Assert.Equal("authentication.unauthorized", ListingSubmissionErrors.Unauthorized.Code);
        Assert.Equal(ErrorType.Unauthorized, ListingSubmissionErrors.Unauthorized.Type);
    }

    private static void AssertValidationFailure(Error? error)
    {
        Assert.NotNull(error);
        Assert.Equal("validation.failed", error.Code);
        Assert.Equal(ErrorType.Validation, error.Type);
    }
}
