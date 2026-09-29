namespace SmartProperty.Migrator;

/// <summary>An operator-actionable migration or provisioning configuration conflict.</summary>
public sealed class MigrationProvisioningException : InvalidOperationException
{
    public MigrationProvisioningException(string message)
        : base(message)
    {
    }

    public MigrationProvisioningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
