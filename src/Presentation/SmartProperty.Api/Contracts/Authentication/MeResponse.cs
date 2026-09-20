#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

public sealed record MeResponse(
    Guid UserId,
    string Email,
    string FirstName,
    string LastName)
{
    // Keeps the user's email and name out of logs and debugger displays of the record. JSON serialization is
    // unaffected, so the response body still carries the full profile.
    public override string ToString()
    {
        return $"{nameof(MeResponse)} {{ {nameof(UserId)} = {UserId} }}";
    }
}
