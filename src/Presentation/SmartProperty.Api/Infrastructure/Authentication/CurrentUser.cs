#nullable enable
using SmartProperty.Application.Abstractions.Identity;

namespace SmartProperty.Api.Infrastructure.Authentication;

internal sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public bool IsAuthenticated => httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true;

    public Guid? UserId
    {
        get
        {
            if (!IsAuthenticated)
            {
                return null;
            }

            return AccessTokenSubject.TryGetUserId(httpContextAccessor.HttpContext!.User, out var userId)
                ? userId
                : null;
        }
    }
}
