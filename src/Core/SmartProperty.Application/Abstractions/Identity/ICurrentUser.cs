#nullable enable
using System;

namespace SmartProperty.Application.Abstractions.Identity;

public interface ICurrentUser
{
    /// <summary>
    /// Current user's id if available; otherwise null for anonymous requests.
    /// </summary>
    Guid? UserId { get; }

    /// <summary>
    /// True when the current request is authenticated.
    /// </summary>
    bool IsAuthenticated { get; }
}
