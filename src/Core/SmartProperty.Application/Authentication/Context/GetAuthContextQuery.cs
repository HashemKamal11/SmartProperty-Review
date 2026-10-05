using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.Context;

public sealed record GetAuthContextQuery : IQuery<AuthContextResult>;
