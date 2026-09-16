#nullable enable
using System.Buffers.Text;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SmartProperty.Api.Infrastructure.Authentication;

/// <summary>
/// Access token subject rules. A token identifies a user only when its payload has exactly one "sub" member
/// whose value is a non-empty Guid in the canonical format issued by <see cref="TokenProvider"/>.
/// </summary>
internal static class AccessTokenSubject
{
    // Matches Guid.ToString() as emitted by TokenProvider.
    private const string SubjectGuidFormat = "D";

    /// <summary>
    /// True when the signed payload contains exactly one top-level "sub" member whose JSON value is a string
    /// holding a canonical non-empty Guid. The token parser collapses duplicate keys and can turn arrays into
    /// scalar claims, so the original JSON shape can only be checked in the raw payload.
    /// </summary>
    public static bool TryGetPayloadUserId(SecurityToken? securityToken, out Guid userId)
    {
        userId = Guid.Empty;

        if (securityToken is not JsonWebToken jsonWebToken)
        {
            return false;
        }

        var reader = new Utf8JsonReader(Base64Url.DecodeFromChars(jsonWebToken.EncodedPayload));

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        var subjectMemberCount = 0;
        string? subject = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isSubject = reader.ValueTextEquals(JwtRegisteredClaimNames.Sub);

            reader.Read();

            if (isSubject)
            {
                subjectMemberCount++;
                subject = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }

            reader.Skip();
        }

        return subjectMemberCount == 1 && TryParseSubject(subject, out userId);
    }

    public static bool TryGetUserId(ClaimsPrincipal? principal, out Guid userId)
    {
        userId = Guid.Empty;

        var subjects = principal?.FindAll(JwtRegisteredClaimNames.Sub).Take(2).ToArray() ?? [];

        return subjects.Length == 1 && TryParseSubject(subjects[0].Value, out userId);
    }

    private static bool TryParseSubject(string? subject, out Guid userId)
    {
        userId = Guid.Empty;

        // Guid parsing tolerates surrounding whitespace; the length check keeps only the exact canonical form.
        if (subject is null
            || subject.Length != Guid.Empty.ToString(SubjectGuidFormat).Length
            || !Guid.TryParseExact(subject, SubjectGuidFormat, out var parsedUserId)
            || parsedUserId == Guid.Empty)
        {
            return false;
        }

        userId = parsedUserId;
        return true;
    }
}
