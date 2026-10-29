# Contact Verification and Password Reset

Registration collects a phone number and an email, and the applicant proves both before an administrator can approve the workspace access request. A forgotten password is reset with a code sent to the account's email. All three flows share one one-time-code mechanism. See [authentication-model.md](authentication-model.md) for registration, login, and tokens.

## One-time codes

A code is six digits, valid for five minutes, and allows five wrong guesses. A new code for the same purpose cannot be requested within 60 seconds of the previous one, and issuing a new code retires the unused ones. The database stores only a SHA-256 hash that covers the user id, the purpose, and the code, so a code issued for one purpose never satisfies another. `identity.one_time_codes` holds the rows; `purpose` is `PhoneVerification`, `EmailVerification`, or `PasswordReset`. The failed-attempt count is a concurrency token, so two parallel wrong guesses cannot both count as one.

The shared logic lives in `Application/Authentication/OneTimeCodes` (`OneTimeCodePolicy`, `OneTimeCodeFlow`, `OneTimeCodeErrors`). The phone, email, and reset handlers only add what is specific to them.

## Registration

`POST /api/auth/register` requires `phoneNumber` in international format (`+`, a country code that does not start with 0, up to 15 digits; spaces, dashes, dots, and parentheses are stripped). The number must be unique. The response is `201` with `userId` and the two expiry times. The phone code is sent through `ISmsSender` and the email code through `IEmailSender`, both after the registration is saved.

| Endpoint | Body | Success | Errors |
|---|---|---|---|
| `POST /api/auth/verify-phone` | `userId`, `code` | `200` with `phoneVerified`, `verifiedAt`, `status`, `message` | `422 phone_verification.invalid_code`, `409 phone_verification.too_many_attempts`, `409 phone_verification.already_verified`, `404 phone_verification.user_not_found` |
| `POST /api/auth/resend-phone-code` | `userId` | `200` with `expiresAt` | `409 phone_verification.resend_too_soon`, `409 phone_verification.already_verified` |
| `POST /api/auth/verify-email` | `userId`, `code` | `200` with `emailVerified`, `verifiedAt`, `status`, `message` | the same codes with the `email_verification.` prefix |
| `POST /api/auth/resend-email-code` | `userId` | `200` with `expiresAt` | the same codes with the `email_verification.` prefix |

Approval of a workspace access request fails with `409 workspace_access_requests.contact_not_verified` until the user's phone and email are both verified. Accounts that have no phone number (created before this feature, or by the bootstrap) are not held back.

## Password reset

| Endpoint | Body | Success |
|---|---|---|
| `POST /api/auth/forgot-password` | `email` | `200` with the same message for every well-formed email |
| `POST /api/auth/reset-password` | `email`, `code`, `newPassword` | `200` with a confirmation message |

- Forgot-password never reveals whether an email is registered. An unknown email, a Suspended or Deactivated account, and a request inside the cooldown all get the same answer, and only a malformed email is refused.
- Reset-password returns one error (`422 password_reset.invalid_code`) for an unknown email, a disabled account, a wrong code, an expired code, or a code of another purpose. An unknown email still pays the password hashing cost.
- A successful reset changes the password, spends the code, and revokes every active refresh token of the account in one save. Access tokens already issued stay valid until they expire, as for logout.
- `newPassword` has no strength rule yet, only the same maximum length as registration.

## Providers

| Channel | Interface | Implementation | Configuration |
|---|---|---|---|
| SMS | `ISmsSender` | `LoggingSmsSender` (delivers nothing; logs the text in Development only) | none yet |
| Email | `IEmailSender` | `SmtpEmailSender` when `Smtp:Host` is set, otherwise `LoggingEmailSender` | `Smtp:Host`, `Port`, `EnableSsl`, `UserName`, `Password`, `FromAddress`, `FromName` |

Set `Smtp:Password` through User Secrets or the `Smtp__Password` environment variable, never in a committed file. The host fails at startup if `Smtp:Host` is set without `Smtp:FromAddress`. If an SMTP send fails, the request fails with a server error; for forgot-password this means an unusable mail server is visible to the caller, which is an operator problem rather than an account lookup.
