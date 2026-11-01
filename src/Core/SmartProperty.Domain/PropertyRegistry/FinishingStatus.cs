namespace SmartProperty.Domain.PropertyRegistry;

/// <summary>
/// How far a property's interior finishing has progressed (حالة التشطيب).
/// </summary>
public enum FinishingStatus
{
    Unfinished = 1,
    SemiFinished = 2,
    FullyFinished = 3,
    SuperLux = 4,
    Furnished = 5
}
