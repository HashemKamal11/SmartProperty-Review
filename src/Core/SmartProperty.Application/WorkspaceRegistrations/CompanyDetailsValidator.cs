using SmartProperty.Common.Results;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceRegistrations;

/// <summary>The company fields of a workspace registration. Returns the first failing rule, or null.</summary>
internal static class CompanyDetailsValidator
{
    public static Error? Validate(string? companyName, string? commercialRegistrationNumber)
    {
        if (string.IsNullOrWhiteSpace(companyName))
        {
            return WorkspaceRegistrationErrors.ValidationFailed("Company name is required.");
        }

        // Checked on the original input: trimming must not silently accept control characters.
        if (companyName.Any(char.IsControl))
        {
            return WorkspaceRegistrationErrors.ValidationFailed("Company name must not contain control characters.");
        }

        if (companyName.Trim().Length > WorkspaceRegistrationRequest.CompanyNameMaxLength)
        {
            return WorkspaceRegistrationErrors.ValidationFailed(
                $"Company name must not exceed {WorkspaceRegistrationRequest.CompanyNameMaxLength} characters.");
        }

        if (!string.IsNullOrWhiteSpace(commercialRegistrationNumber))
        {
            if (commercialRegistrationNumber.Any(char.IsControl))
            {
                return WorkspaceRegistrationErrors.ValidationFailed(
                    "Commercial registration number must not contain control characters.");
            }

            if (commercialRegistrationNumber.Trim().Length > WorkspaceRegistrationRequest.CommercialRegistrationNumberMaxLength)
            {
                return WorkspaceRegistrationErrors.ValidationFailed(
                    $"Commercial registration number must not exceed {WorkspaceRegistrationRequest.CommercialRegistrationNumberMaxLength} characters.");
            }
        }

        return null;
    }
}
