#nullable enable
namespace SmartProperty.Api.Infrastructure.Errors;

internal static class ApiErrorCodes
{
    public const string MalformedRequest = "request.malformed";
    public const string UnexpectedFailure = "server.unexpected_error";
}
