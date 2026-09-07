#nullable enable
namespace SmartProperty.Common.Results;

public sealed record Error(string Code, string Description, ErrorType Type);
