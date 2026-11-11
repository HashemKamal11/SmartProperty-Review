using SmartProperty.Domain.OwnerVerification;
using Xunit;

namespace SmartProperty.UnitTests.Domain.OwnerVerification;

public sealed class OwnerProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid Reviewer = Guid.NewGuid();

    // ---- identity step

    [Fact]
    public void ANewProfileIsADraftWithNothingFilledIn()
    {
        var profile = NewProfile();

        Assert.Equal(OwnerVerificationStatus.Draft, profile.Status);
        Assert.False(profile.IsIdentityComplete);
        Assert.False(profile.IsAddressComplete);
    }

    [Fact]
    public void ValidIdentityIsStoredAndCompletesTheStep()
    {
        var profile = NewProfile();

        var wasApproved = profile.SaveIdentity(" sa ", "1234567890", " Saudi ", Today.AddYears(-30), Today.AddYears(5), true, Today, Now);

        Assert.False(wasApproved);
        Assert.True(profile.IsIdentityComplete);
        Assert.Equal("SA", profile.IdCountryCode);
        Assert.Equal("1234567890", profile.NationalId);
        Assert.Equal("Saudi", profile.Nationality);
    }

    [Theory]
    [InlineData("SA", "1234567890")] // Saudi citizen
    [InlineData("SA", "2234567890")] // Iqama (resident)
    [InlineData("AE", "784199012345671")]
    [InlineData("AE", "784-1990-1234567-1")] // as printed on the card
    public void SaudiAndEmiratiIdsAreAccepted(string country, string nationalId)
    {
        var profile = NewProfile();

        SaveIdentity(profile, country: country, nationalId: nationalId);

        Assert.Equal(country, profile.IdCountryCode);
        Assert.Equal(nationalId.Replace("-", "", StringComparison.Ordinal), profile.NationalId);
    }

    [Theory]
    [InlineData("29001150101234", 1990, 1, 15)]
    [InlineData("30502280101234", 2005, 2, 28)]
    [InlineData("2 900115 01 0123 4", 1990, 1, 15)]
    public void AnEgyptianIdWhoseEncodedBirthDateMatchesIsAccepted(string nationalId, int year, int month, int day)
    {
        var profile = NewProfile();

        SaveIdentity(profile, country: "EG", nationalId: nationalId, birthDate: new DateOnly(year, month, day));

        Assert.Equal("EG", profile.IdCountryCode);
        Assert.Equal(nationalId.Replace(" ", "", StringComparison.Ordinal), profile.NationalId);
    }

    [Theory]
    [InlineData("SA", "")]
    [InlineData("SA", "123456789")]
    [InlineData("SA", "12345678901")]
    [InlineData("SA", "12345abcde")]
    [InlineData("SA", "١٢٣٤٥٦٧٨٩٠")]
    [InlineData("SA", "3234567890")] // neither citizen (1) nor resident (2)
    [InlineData("AE", "78419901234567")] // 14 digits
    [InlineData("AE", "123199012345671")] // not 784
    [InlineData("EG", "2900115010123")] // 13 digits
    [InlineData("EG", "19001150101234")] // century digit 1
    [InlineData("EG", "29013150101234")] // month 13
    [InlineData("EG", "29002300101234")] // 30 February
    [InlineData("SA", "29001150101234")] // an Egyptian ID declared as Saudi
    [InlineData("EG", "1234567890")] // a Saudi ID declared as Egyptian
    public void AnIdThatDoesNotMatchItsCountryIsRefused(string country, string nationalId)
    {
        var profile = NewProfile();

        Assert.Throws<ArgumentException>(
            () => SaveIdentity(profile, country: country, nationalId: nationalId, birthDate: new DateOnly(1990, 1, 15)));
        Assert.Null(profile.NationalId);
    }

    [Fact]
    public void AnEgyptianIdWhoseEncodedBirthDateDiffersIsRefused()
    {
        var profile = NewProfile();

        var exception = Assert.Throws<ArgumentException>(
            () => SaveIdentity(profile, country: "EG", nationalId: "29001150101234", birthDate: new DateOnly(1990, 1, 16)));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("KSA")]
    public void AnUnsupportedIdCountryIsRefused(string country)
    {
        var profile = NewProfile();

        Assert.Throws<ArgumentException>(() => SaveIdentity(profile, country: country));
    }

    [Fact]
    public void AnOwnerUnderEighteenIsRefused()
    {
        var profile = NewProfile();

        Assert.Throws<ArgumentException>(() => SaveIdentity(profile, birthDate: Today.AddYears(-17)));
        SaveIdentity(profile, birthDate: Today.AddYears(-18));
    }

    [Fact]
    public void AnExpiredIdIsRefused()
    {
        var profile = NewProfile();

        Assert.Throws<ArgumentException>(() => SaveIdentity(profile, idExpiryDate: Today));
        Assert.Throws<ArgumentException>(() => SaveIdentity(profile, idExpiryDate: Today.AddDays(-1)));
    }

    [Fact]
    public void WithoutTheConfirmationNothingIsSaved()
    {
        var profile = NewProfile();

        Assert.Throws<ArgumentException>(() => SaveIdentity(profile, confirmed: false));
        Assert.Null(profile.NationalId);
    }

    // ---- address step

    [Fact]
    public void ValidAddressIsStored()
    {
        var profile = NewProfile();

        SaveAddress(profile);

        Assert.Equal("1234", profile.BuildingNumber);
        Assert.Equal("Al Olaya", profile.StreetName);
        Assert.Equal("12345", profile.PostalCode);
        Assert.Equal("5678", profile.AdditionalNumber);
    }

    [Theory]
    [InlineData("123", "12345", "5678")]
    [InlineData("12345", "12345", "5678")]
    [InlineData("1234", "1234", "5678")]
    [InlineData("1234", "123456", "5678")]
    [InlineData("1234", "12345", "567")]
    [InlineData("1234", "12345", "56789")]
    [InlineData("12a4", "12345", "5678")]
    public void NumbersOfTheWrongLengthOrWithLettersAreRefused(string building, string postal, string additional)
    {
        var profile = NewProfile();

        Assert.Throws<ArgumentException>(() => SaveAddress(profile, building: building, postal: postal, additional: additional));
    }

    [Theory]
    [InlineData("", "District", "Riyadh")]
    [InlineData("Street", " ", "Riyadh")]
    [InlineData("Street", "District", "")]
    public void StreetDistrictAndCityAreRequired(string street, string district, string city)
    {
        var profile = NewProfile();

        Assert.Throws<ArgumentException>(() => SaveAddress(profile, street: street, district: district, city: city));
    }

    [Fact]
    public void TheProofImageCompletesTheAddressStep()
    {
        var profile = NewProfile();
        SaveAddress(profile);
        Assert.False(profile.IsAddressComplete);

        profile.SetAddressProof("owner-profiles/x/proof.png", "image/png", "card.png", Now);

        Assert.True(profile.IsAddressComplete);
        Assert.Equal("card.png", profile.AddressProofFileName);
    }

    // ---- submit

    [Fact]
    public void AnIncompleteProfileCannotBeSubmitted()
    {
        var profile = NewProfile();
        Assert.Throws<InvalidOperationException>(() => profile.Submit(Now));

        SaveIdentity(profile);
        Assert.Throws<InvalidOperationException>(() => profile.Submit(Now));

        SaveAddress(profile);
        Assert.Throws<InvalidOperationException>(() => profile.Submit(Now));

        profile.SetAddressProof("k", "image/png", null, Now);
        profile.Submit(Now);

        Assert.Equal(OwnerVerificationStatus.UnderReview, profile.Status);
        Assert.Equal(Now, profile.SubmittedAt);
    }

    [Fact]
    public void AProfileUnderReviewCannotBeEdited()
    {
        var profile = Submitted();

        Assert.True(profile.IsLockedForEditing);
        Assert.Throws<InvalidOperationException>(() => SaveIdentity(profile));
        Assert.Throws<InvalidOperationException>(() => SaveAddress(profile));
        Assert.Throws<InvalidOperationException>(() => profile.SetAddressProof("k2", "image/png", null, Now));
        Assert.Throws<InvalidOperationException>(() => profile.Submit(Now));
    }

    // ---- decisions

    [Fact]
    public void ApprovingRecordsTheReviewerAndTheTime()
    {
        var profile = Submitted();

        profile.Approve(Reviewer, Now.AddHours(1));

        Assert.Equal(OwnerVerificationStatus.Approved, profile.Status);
        Assert.Equal(Reviewer, profile.ReviewedByUserId);
        Assert.Equal(Now.AddHours(1), profile.ReviewedAt);
        Assert.Null(profile.RejectionReason);
    }

    [Fact]
    public void RejectingNeedsAReasonAndKeepsIt()
    {
        var profile = Submitted();

        Assert.Throws<ArgumentException>(() => profile.Reject(Reviewer, "  ", Now));
        Assert.Throws<ArgumentException>(() => profile.Reject(Reviewer, new string('x', OwnerProfile.MaxRejectionReasonLength + 1), Now));

        profile.Reject(Reviewer, "  The ID photo is unreadable.  ", Now.AddHours(1));

        Assert.Equal(OwnerVerificationStatus.Rejected, profile.Status);
        Assert.Equal("The ID photo is unreadable.", profile.RejectionReason);
    }

    [Fact]
    public void OnlyAProfileUnderReviewCanBeDecided()
    {
        var draft = NewProfile();
        Assert.Throws<InvalidOperationException>(() => draft.Approve(Reviewer, Now));
        Assert.Throws<InvalidOperationException>(() => draft.Reject(Reviewer, "no", Now));

        var approved = Submitted();
        approved.Approve(Reviewer, Now);
        Assert.Throws<InvalidOperationException>(() => approved.Approve(Reviewer, Now));
        Assert.Throws<InvalidOperationException>(() => approved.Reject(Reviewer, "no", Now));
    }

    [Fact]
    public void EditingARejectedProfileReturnsItToDraftAndClearsTheDecision()
    {
        var profile = Submitted();
        profile.Reject(Reviewer, "Fix the address.", Now);

        var wasApproved = SaveAddress(profile, street: "Another street");

        Assert.False(wasApproved);
        Assert.Equal(OwnerVerificationStatus.Draft, profile.Status);
        Assert.Null(profile.RejectionReason);
        Assert.Null(profile.ReviewedByUserId);
        Assert.Null(profile.SubmittedAt);
    }

    [Fact]
    public void EditingAnApprovedProfileReturnsItToDraftAndReportsTheGrantMustGo()
    {
        var profile = Submitted();
        profile.Approve(Reviewer, Now);

        var wasApproved = SaveIdentity(profile, nationality: "Egyptian");

        Assert.True(wasApproved);
        Assert.Equal(OwnerVerificationStatus.Draft, profile.Status);
        Assert.Null(profile.ReviewedAt);
    }

    // ---- helpers

    private static OwnerProfile NewProfile() => new(Guid.NewGuid(), UserId, Now);

    private static OwnerProfile Submitted()
    {
        var profile = NewProfile();
        SaveIdentity(profile);
        SaveAddress(profile);
        profile.SetAddressProof("k", "image/png", "card.png", Now);
        profile.Submit(Now);

        return profile;
    }

    private static bool SaveIdentity(
        OwnerProfile profile,
        string nationalId = "1234567890",
        string nationality = "Saudi",
        DateOnly? birthDate = null,
        DateOnly? idExpiryDate = null,
        bool confirmed = true,
        string country = "SA")
    {
        return profile.SaveIdentity(
            country,
            nationalId,
            nationality,
            birthDate ?? Today.AddYears(-30),
            idExpiryDate ?? Today.AddYears(5),
            confirmed,
            Today,
            Now);
    }

    private static bool SaveAddress(
        OwnerProfile profile,
        string building = "1234",
        string street = "Al Olaya",
        string district = "Al Malqa",
        string city = "Riyadh",
        string postal = "12345",
        string additional = "5678",
        bool confirmed = true)
    {
        return profile.SaveAddress(building, street, district, city, postal, additional, confirmed, Now);
    }
}
