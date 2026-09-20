using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.Me;

/// <summary>
/// Reads the current user's profile. The query carries no input: the identity comes only from the validated
/// principal through <see cref="Abstractions.Identity.ICurrentUser"/>, never from the request.
/// </summary>
public sealed record GetMeQuery : IQuery<MeResult>;
