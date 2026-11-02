namespace SmartProperty.Domain.PropertyRegistry;

/// <summary>
/// How far a property's interior finishing has progressed (حالة التشطيب). The set mirrors the closed list of the AI
/// search service. Whether the property is furnished is a separate yes/no fact, not a finishing level.
/// </summary>
public enum FinishingStatus
{
    /// <summary>تشطيب كامل</summary>
    Finished = 1,

    /// <summary>تشطيب فاخر</summary>
    LuxuryFinished = 2,

    /// <summary>نصف تشطيب</summary>
    SemiFinished = 3,

    /// <summary>على الطوب الأحمر</summary>
    CoreAndShell = 4
}
