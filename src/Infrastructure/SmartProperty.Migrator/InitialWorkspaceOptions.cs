namespace SmartProperty.Migrator;

/// <summary>Configuration for the one optional workspace established during deployment.</summary>
public sealed class InitialWorkspaceOptions
{
    public const string SectionName = "Provisioning:InitialWorkspace";

    public bool Enabled { get; set; }

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}
