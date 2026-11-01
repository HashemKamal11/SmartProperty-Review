using SmartProperty.Application.Authentication.Register;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.OneTimeCodes;

/// <summary>
/// Rules for who can ask for, and be sent, a one-time code. The flows identify an account by its email, which
/// anyone can type, so none of them may answer differently for an unknown email than for a known one.
/// </summary>
internal static class CodeRecipient
{
    /// <summary>
    /// Refuses only a malformed email, which says nothing about any account.
    /// </summary>
    public static Error? ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return OneTimeCodeErrors.ValidationFailed("Email is required.");
        }

        var trimmed = email.Trim();

        if (trimmed.Length > RegisterCommandValidator.EmailMaxLength
            || !RegisterCommandValidator.HasEmailStructure(trimmed))
        {
            return OneTimeCodeErrors.ValidationFailed("Email must be a valid email address.");
        }

        return null;
    }

    /// <summary>
    /// Refuses only a number that is not in international format, which says nothing about any account. The
    /// normalized number is what the account is looked up by.
    /// </summary>
    public static Error? ValidatePhoneNumber(string phoneNumber, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            normalized = string.Empty;

            return OneTimeCodeErrors.ValidationFailed("Phone number is required.");
        }

        if (!PhoneNumberFormat.TryNormalize(phoneNumber, out normalized))
        {
            return OneTimeCodeErrors.ValidationFailed(
                "Phone number must be in international format, for example +201012345678.");
        }

        return null;
    }

    /// <summary>
    /// A Pending account is included on purpose: it is exactly the account that still has to verify. Suspended and
    /// Deactivated accounts get nothing.
    /// </summary>
    public static bool CanReceiveCodes(User user)
    {
        return user.Status is UserStatus.Pending or UserStatus.Active;
    }
}
