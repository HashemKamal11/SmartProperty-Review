#nullable enable
using SmartProperty.Application.Abstractions.Time;

namespace SmartProperty.Api.Infrastructure.Time;

internal sealed class DateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
