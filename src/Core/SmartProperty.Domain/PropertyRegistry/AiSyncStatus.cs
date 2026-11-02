namespace SmartProperty.Domain.PropertyRegistry;

/// <summary>Whether the AI search service currently holds this property.</summary>
public enum AiSyncStatus
{
    /// <summary>Nothing is held there: never sent, not sendable yet (see the sync error), or removed.</summary>
    NotSynced = 1,

    /// <summary>The AI service holds the property as last saved.</summary>
    Synced = 2,

    /// <summary>The last attempt to send it failed; the sync error says why and a retry is safe.</summary>
    Failed = 3
}
