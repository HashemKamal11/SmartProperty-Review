#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authentication.Login;
using SmartProperty.Application.Authentication.Logout;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Application.Authentication.Refresh;
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
            request.WorkspaceId ?? Guid.Empty);

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
            registration.WorkspaceId,
            registration.WorkspaceAccessRequestId,
            registration.WorkspaceAccessRequestStatus.ToString());

        // No endpoint can read a registration back yet, so the response has no Location header.
        return StatusCode(StatusCodes.Status201Created, response);
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
}
