#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authentication.Login;
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
}
