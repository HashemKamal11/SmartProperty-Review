#nullable enable
using Microsoft.Extensions.Options;
using SmartProperty.Application.Abstractions.Authentication;

namespace SmartProperty.Api.Infrastructure.Authentication;

/// <summary>
/// Settings from the "ContactVerification" section. <see cref="AutoVerifyPhone"/> is a temporary switch for a
/// deployment with no SMS provider: when true, registration marks the phone number verified without proving it.
/// Turn it off (the default) as soon as real SMS delivery exists.
/// </summary>
public sealed class ContactVerificationOptions
{
    public const string SectionName = "ContactVerification";

    public bool AutoVerifyPhone { get; set; }
}

internal sealed class ConfiguredContactVerificationPolicy(
    IOptions<ContactVerificationOptions> options,
    ILogger<ConfiguredContactVerificationPolicy> logger) : IContactVerificationPolicy
{
    public bool AutoVerifyPhone
    {
        get
        {
            var enabled = options.Value.AutoVerifyPhone;

            if (enabled)
            {
                logger.LogWarning(
                    "ContactVerification:AutoVerifyPhone is on: a registered phone number is treated as verified " +
                    "without a code. Turn it off once an SMS provider is in place.");
            }

            return enabled;
        }
    }
}
