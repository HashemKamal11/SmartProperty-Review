# Contact Verification and Password Reset

Registration collects a phone number and an email, and the applicant proves both before an administrator can approve the workspace access request. A forgotten password is reset with a code sent to the account's email. All three flows share one one-time-code mechanism. See [authentication-model.md](authentication-model.md) for registration, login, and tokens.

## One-time codes

A code is six digits, valid for five minutes, and allows five wrong guesses. A new code for the same purpose cannot be requested within 60 seconds of the previous one, and issuing a new code retires the unused ones. The database stores only a SHA-256 hash that covers the user id, the purpose, and the code, so a code issued for one purpose never satisfies another. `identity.one_time_codes` holds the rows; `purpose` is `PhoneVerification`, `EmailVerification`, or `PasswordReset`. The failed-attempt count is a concurrency token, so two parallel wrong guesses cannot both count as one.

The shared logic lives in `Application/Authentication/OneTimeCodes` (`OneTimeCodePolicy`, `OneTimeCodeFlow`, `OneTimeCodeErrors`). The phone, email, and reset handlers only add what is specific to them.

## Registration

`POST /api/auth/register` requires `phoneNumber` in international format (`+`, a country code that does not start with 0, up to 15 digits; spaces, dashes, dots, and parentheses are stripped). The number must be unique. The response is `201` with `userId` and the two expiry times. The phone code is sent through `ISmsSender` and the email code through `IEmailSender`, both after the registration is saved.

### Temporary: auto-verifying the phone

While no SMS provider is integrated, set `ContactVerification:AutoVerifyPhone` to `true` (environment variable `ContactVerification__AutoVerifyPhone`, or `AUTO_VERIFY_PHONE=true` in `.env` for Docker). Registration then marks the phone number verified at once: no phone code is stored, no SMS is sent, and `phoneVerificationExpiresAt` in the response is `null`. The email is unchanged: it still gets a code, and verifying it (`verify-email`) activates the account. The setting is off by default and the API logs a warning each time it is used.

This proves nothing about the number, so anyone can register with a phone they do not own, and the "both contacts verified" rule that gates workspace access approval is then effectively an email check. Turn it off as soon as real SMS delivery is in place. Accounts registered while it was on stay phone-verified.

| Endpoint | Body | Success | Errors |
|---|---|---|---|
| `POST /api/auth/verify-phone` | `phoneNumber`, `code` | `200` with `userId`, `phoneVerified`, `verifiedAt`, `status`, `message` | `422 phone_verification.invalid_code`, `409 phone_verification.too_many_attempts` |
| `POST /api/auth/resend-phone-code` | `phoneNumber` | `200` with the same message for every well-formed number | `422 validation.failed` for a number not in international format |
| `POST /api/auth/verify-email` | `email`, `code` | `200` with `userId`, `emailVerified`, `verifiedAt`, `status`, `message` | the same codes with the `email_verification.` prefix |
| `POST /api/auth/resend-email-code` | `email` | `200` with the same message for every well-formed email | `422 validation.failed` for a malformed email |

Each code is identified by where it was sent: the phone endpoints take the phone number and the email endpoints take the email, so an applicant whose code expired can ask for a new one without the `userId` from registration. Resend works like forgot-password: an unknown email or number, an already verified phone or email, a disabled account, and a request inside the 60-second cooldown all get the same `200` and send nothing, so the endpoints cannot be used to find registered emails or numbers. Verify returns one error (`invalid_code`) for an unknown email or number, an already verified contact, a wrong code, and an expired code. Pending and Active accounts can use these endpoints; Suspended and Deactivated ones cannot.

Approval of a workspace access request fails with `409 workspace_access_requests.contact_not_verified` until the user's phone and email are both verified. Accounts that have no phone number (created before this feature, or by the bootstrap) are not held back.

## Password reset

Three steps, one per screen: the email, the code on its own, then the new password.

| Step | Endpoint | Body | Success |
|---|---|---|---|
| 1 | `POST /api/auth/forgot-password` | `email` | `200` with the same message for every well-formed email |
| 2 | `POST /api/auth/verify-reset-code` | `email`, `code` | `200` with `resetToken` and `expiresAt` |
| 3 | `POST /api/auth/reset-password` | `email`, `resetToken`, `newPassword`, `confirmPassword` | `200` with a confirmation message |

Step 2 checks the six-digit code (five minutes, five wrong guesses), spends it, and in the same save issues the `resetToken`: 256 random bits (43 URL-safe characters), stored only as a hash under the purpose `PasswordResetGrant`, valid for ten minutes and usable once. Verifying again issues a new token and retires the earlier one. Step 3 takes no code. `confirmPassword` must equal `newPassword`, and the password needs 8 to 128 characters.

- Forgot-password never reveals whether an email is registered. An unknown email, a Suspended or Deactivated account, and a request inside the cooldown all get the same answer, and only a malformed email is refused.
- Verify-reset-code returns one error (`422 password_reset.invalid_code`) for an unknown email, a disabled account, a wrong code, an expired code, or a code of another purpose; `409 password_reset.too_many_attempts` after five wrong codes.
- Reset-password returns one error (`422 password_reset.invalid_token`) for an unknown email, a disabled account, a wrong, expired or already used token, or the token of another account. An unknown email still pays the password hashing cost. A mismatched confirmation or a bad password is `422 validation.failed` and leaves the token usable.
- A successful reset changes the password, spends the code, and revokes every active refresh token of the account in one save. Access tokens already issued stay valid until they expire, as for logout.
- The old single-call shape (`email`, `code`, `newPassword`) is gone: a code sent to reset-password is refused as an invalid token.

## Providers

| Channel | Interface | Implementation | Configuration |
|---|---|---|---|
| SMS | `ISmsSender` | `LoggingSmsSender` (delivers nothing; logs the text in Development only) | none yet |
| Email | `IEmailSender` | `SmtpEmailSender` when `Smtp:Host` is set, otherwise `LoggingEmailSender` | `Smtp:Host`, `Port`, `EnableSsl`, `UserName`, `Password`, `FromAddress`, `FromName` |

Set `Smtp:Password` through User Secrets or the `Smtp__Password` environment variable, never in a committed file. The host fails at startup if `Smtp:Host` is set without `Smtp:FromAddress`. If an SMTP send fails, the request fails with a server error; for forgot-password this means an unusable mail server is visible to the caller, which is an operator problem rather than an account lookup.
