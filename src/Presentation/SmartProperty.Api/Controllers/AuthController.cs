#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authentication.Login;
using SmartProperty.Application.Authentication.Logout;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Application.Authentication.Context;
using SmartProperty.Application.Authentication.Refresh;
using SmartProperty.Application.Authentication.EmailVerifications;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Application.Authentication.PasswordReset;
using SmartProperty.Application.Authentication.PhoneVerifications;
using SmartProperty.Application.Authentication.Register;

namespace SmartProperty.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest request,
        [FromServices] ICommandHandler<RegisterCommand, RegisterResult> handler,
        CancellationToken cancellationToken)
    {
        var command = new RegisterCommand(
            request.Email ?? string.Empty,
            request.Password ?? string.Empty,
            request.FirstName ?? string.Empty,
            request.LastName ?? string.Empty,
            request.PhoneNumber ?? string.Empty);

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        var registration = result.Value;
        var response = new RegisterResponse(
            registration.UserId,
            registration.UserStatus.ToString(),
            registration.PhoneVerificationExpiresAt,
            registration.EmailVerificationExpiresAt);

        // No endpoint can read a registration back yet, so the response has no Location header.
        return StatusCode(StatusCodes.Status201Created, response);
    }

    // The account is identified by its email: the caller has no token yet, and the one-time code is the proof.
    [AllowAnonymous]
    [HttpPost("verify-phone")]
    public async Task<IActionResult> VerifyPhone(
        [FromBody] VerifyPhoneRequest request,
        [FromServices] ICommandHandler<VerifyPhoneCommand, PhoneVerified> handler,
        CancellationToken cancellationToken)
    {
        var command = new VerifyPhoneCommand(
            request.PhoneNumber ?? string.Empty,
            request.Code ?? string.Empty);

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        var verified = result.Value;

        return Ok(new VerifyPhoneResponse(
            verified.UserId,
            PhoneVerified: true,
            verified.VerifiedAt,
            verified.UserStatus.ToString(),
            "Phone number verified. Your account is awaiting administrator approval."));
    }

    [AllowAnonymous]
    [HttpPost("resend-phone-code")]
    public async Task<IActionResult> ResendPhoneCode(
        [FromBody] ResendPhoneVerificationRequest request,
        [FromServices] ICommandHandler<ResendPhoneVerificationCommand> handler,
        CancellationToken cancellationToken)
    {
        var command = new ResendPhoneVerificationCommand(request.PhoneNumber ?? string.Empty);

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(new ResendCodeResponse(
            "If this phone number needs verifying, a new verification code has been sent to it."));
    }

    [AllowAnonymous]
    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail(
        [FromBody] VerifyEmailRequest request,
        [FromServices] ICommandHandler<VerifyEmailCommand, EmailVerified> handler,
        CancellationToken cancellationToken)
    {
        var command = new VerifyEmailCommand(
            request.Email ?? string.Empty,
            request.Code ?? string.Empty);

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        var verified = result.Value;

        return Ok(new VerifyEmailResponse(
            verified.UserId,
            EmailVerified: true,
            verified.VerifiedAt,
            verified.UserStatus.ToString(),
            "Email verified. Your account is awaiting administrator approval."));
    }

    [AllowAnonymous]
    [HttpPost("resend-email-code")]
    public async Task<IActionResult> ResendEmailCode(
        [FromBody] ResendEmailVerificationRequest request,
        [FromServices] ICommandHandler<ResendEmailVerificationCommand> handler,
        CancellationToken cancellationToken)
    {
        var command = new ResendEmailVerificationCommand(request.Email ?? string.Empty);

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(new ResendCodeResponse(
            "If this email needs verifying, a new verification code has been sent to it."));
    }

    // Answers the same for every well-formed email, whether or not an account uses it.
    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest request,
        [FromServices] ICommandHandler<ForgotPasswordCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ForgotPasswordCommand(request.Email ?? string.Empty), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(new ForgotPasswordResponse(
            "If an account uses this email, a password reset code has been sent to it."));
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest request,
        [FromServices] ICommandHandler<ResetPasswordCommand> handler,
        CancellationToken cancellationToken)
    {
        var command = new ResetPasswordCommand(
            request.Email ?? string.Empty,
            request.Code ?? string.Empty,
            request.NewPassword ?? string.Empty);

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        return Ok(new ResetPasswordResponse("Password changed. Sign in with your new password."));
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        [FromServices] ICommandHandler<LoginCommand, LoginResult> handler,
        CancellationToken cancellationToken)
    {
        var command = new LoginCommand(
            request.Email ?? string.Empty,
            request.Password ?? string.Empty);

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        var login = result.Value;
        var response = new LoginResponse(
            login.AccessToken,
            login.AccessTokenExpiresAt,
            login.RefreshToken,
            login.RefreshTokenExpiresAt);

        return Ok(response);
    }

    // The refresh token is itself the credential, and the access token it replaces may already have expired,
    // so this action must not require a Bearer token.
    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshRequest request,
        [FromServices] ICommandHandler<RefreshCommand, RefreshResult> handler,
        CancellationToken cancellationToken)
    {
        var command = new RefreshCommand(request.RefreshToken ?? string.Empty);

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        var refresh = result.Value;
        var response = new RefreshResponse(
            refresh.AccessToken,
            refresh.AccessTokenExpiresAt,
            refresh.RefreshToken,
            refresh.RefreshTokenExpiresAt);

        return Ok(response);
    }

    // The refresh token being revoked is itself the credential, so no Bearer token is required: the caller's
    // access token may already have expired, and an account that can no longer sign in must still be able to
    // destroy a session.
    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(
        [FromBody] LogoutRequest request,
        [FromServices] ICommandHandler<LogoutCommand> handler,
        CancellationToken cancellationToken)
    {
        var command = new LogoutCommand(request.RefreshToken ?? string.Empty);

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        // No body: the response must not reveal whether anything was actually revoked.
        return NoContent();
    }

    // The first protected endpoint. The identity comes only from the validated principal: no user id, email,
    // or subject is accepted from the route, query string, headers, or a body.
    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me(
        [FromServices] IQueryHandler<GetMeQuery, MeResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetMeQuery(), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);

            return StatusCode(errorResponse.Status, errorResponse);
        }

        var me = result.Value;
        var response = new MeResponse(
            me.UserId,
            me.Email,
            me.FirstName,
            me.LastName);

        return Ok(response);
    }

    [Authorize]
    [HttpGet("context")]
    [ProducesResponseType<AuthContextResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Context(
        [FromServices] IQueryHandler<GetAuthContextQuery, AuthContextResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetAuthContextQuery(), cancellationToken);

        if (result.IsFailure)
        {
            var errorResponse = ApiErrorResponseFactory.FromError(result.Error!, HttpContext);
            return StatusCode(errorResponse.Status, errorResponse);
        }

        var context = result.Value;
        var response = new AuthContextResponse(
            new AuthContextUserResponse(
                context.User.Id,
                context.User.Email,
                context.User.FirstName,
                context.User.LastName),
            context.PlatformRoles,
            context.PlatformPermissions,
            context.Workspaces
                .Select(workspace => new AuthContextWorkspaceResponse(
                    workspace.Id,
                    workspace.Name,
                    workspace.Roles,
                    workspace.Permissions))
                .ToArray());

        Response.Headers.CacheControl = "no-store";
        return Ok(response);
    }
}
