#nullable enable
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Api.Contracts.Marketplace.Owner;

/// <summary>
/// Turns request shapes into commands. It only parses: every business rule (positive amounts, currencies, window
/// ordering, time zones) is the Application validator's, so a rule lives in one place.
/// </summary>
internal static class OwnerListingMapper
{
    public static Error Invalid(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }

    public static Result<ListingPurpose> ParsePurpose(string? text)
    {
        return TryParseEnum<ListingPurpose>(text, out var purpose)
            ? Result<ListingPurpose>.Success(purpose)
            : Result<ListingPurpose>.Failure(Invalid("The listing purpose must be Sale or Rent."));
    }

    public static Result<ListingDraftContentInput> ToInput(ListingDraftContentRequest? request)
    {
        if (request is null)
        {
            return Result<ListingDraftContentInput>.Failure(Invalid("The listing content is required."));
        }

        RentPeriod? period = null;

        if (!string.IsNullOrWhiteSpace(request.RentPeriod))
        {
            if (!TryParseEnum<RentPeriod>(request.RentPeriod, out var parsed))
            {
                return Result<ListingDraftContentInput>.Failure(
                    Invalid("The rent period must be Daily, Weekly, Monthly or Yearly."));
            }

            period = parsed;
        }

        List<RentPricingOptionInput>? options = null;

        if (request.PricingOptions is not null)
        {
            options = [];

            foreach (var option in request.PricingOptions)
            {
                if (option is null || !TryParseEnum<RentPeriod>(option.Period, out var optionPeriod))
                {
                    return Result<ListingDraftContentInput>.Failure(
                        Invalid("Each pricing option needs a period of Daily, Weekly, Monthly or Yearly."));
                }

                options.Add(new RentPricingOptionInput(optionPeriod, option.Amount ?? 0m, option.Currency ?? string.Empty));
            }
        }

        RentalTermsInput? terms = null;

        if (request.RentalTerms is { } requested)
        {
            var windows = new List<RentalWindowInput>();

            foreach (var window in requested.Windows ?? [])
            {
                if (window?.StartDate is not { } start || window.EndDate is not { } end)
                {
                    return Result<ListingDraftContentInput>.Failure(
                        Invalid("Each availability window needs a start date and an end date."));
                }

                windows.Add(new RentalWindowInput(start, end));
            }

            if (requested.CheckInTime is not { } checkIn || requested.CheckOutTime is not { } checkOut)
            {
                return Result<ListingDraftContentInput>.Failure(
                    Invalid("The rental terms need a check-in time and a check-out time."));
            }

            terms = new RentalTermsInput(checkIn, checkOut, windows, requested.MinimumStayDays);
        }

        return Result<ListingDraftContentInput>.Success(new ListingDraftContentInput(
            request.Title ?? string.Empty,
            request.Description ?? string.Empty,
            request.Amount ?? 0m,
            request.Currency ?? string.Empty,
            period,
            request.EvidenceIds ?? [],
            options,
            terms));
    }

    public static ListingDraftResponse ToResponse(ListingDraftResult result)
    {
        return new ListingDraftResponse(result.SubmissionId, result.ListingId, result.Version, result.Revision);
    }

    // Names only: a number never selects an enum member, so "1" and "99" are refused like any other unknown value.
    private static bool TryParseEnum<T>(string? text, out T value)
        where T : struct, Enum
    {
        value = default;

        return !string.IsNullOrWhiteSpace(text)
            && !char.IsDigit(text.Trim()[0])
            && Enum.TryParse(text.Trim(), ignoreCase: true, out value)
            && Enum.IsDefined(value);
    }
}
