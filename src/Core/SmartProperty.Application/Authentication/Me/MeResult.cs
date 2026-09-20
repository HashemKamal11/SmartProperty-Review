namespace SmartProperty.Application.Authentication.Me;

/// <summary>
/// The current user's basic identity and profile, read from the database. Carries no credential, token, role,
/// permission, or workspace data.
/// </summary>
public sealed record MeResult(
    Guid UserId,
    string Email,
    string FirstName,
    string LastName)
{
    // Keeps the user's email and name out of logs and debugger displays of the record. The id is retained
    // because it is already the subject of the access token and is what makes a log entry useful.
    public override string ToString()
    {
        return $"{nameof(MeResult)} {{ {nameof(UserId)} = {UserId} }}";
    }
}
