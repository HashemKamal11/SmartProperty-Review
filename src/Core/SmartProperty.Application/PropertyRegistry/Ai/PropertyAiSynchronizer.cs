using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Ai;

public sealed class PropertyAiSynchronizer(
    IAiSearchGateway gateway,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : IPropertyAiSynchronizer
{
    /// <summary>The listing's external id in the AI service: the property's own id.</summary>
    public static string ExternalId(Property property) => property.Id.ToString();

    public async Task SyncAsync(Property property, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);

        var blockers = AiListingMapper.Blockers(property);

        if (blockers.Count > 0)
        {
            // A copy sent earlier would otherwise keep answering searches with data the owner has since removed.
            if (property.AiSyncStatus != AiSyncStatus.NotSynced)
            {
                await gateway.DeleteListingAsync(ExternalId(property), cancellationToken);
            }

            property.MarkAiNotSynced($"Not sent to AI search: {string.Join("; ", blockers)}.");
        }
        else
        {
            var result = await gateway.PutListingAsync(
                ExternalId(property),
                AiListingMapper.ToListing(property),
                cancellationToken);

            if (result.IsSuccess)
            {
                property.MarkAiSynced(dateTimeProvider.UtcNow);
            }
            else if (result.Error!.Code == AiSearchErrors.DisabledCode)
            {
                property.MarkAiNotSynced(result.Error.Description);
            }
            else
            {
                property.MarkAiSyncFailed(result.Error.Description);
            }
        }

        await SaveOutcomeAsync(cancellationToken);
    }

    public async Task RemoveAsync(Property property, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);

        // Nothing was ever sent, so there is nothing to take down.
        if (property.AiSyncStatus == AiSyncStatus.NotSynced)
        {
            return;
        }

        var result = await gateway.DeleteListingAsync(ExternalId(property), cancellationToken);

        if (result.IsSuccess)
        {
            property.MarkAiNotSynced("Removed from AI search because the property was archived.");
        }
        else if (result.Error!.Code == AiSearchErrors.DisabledCode)
        {
            property.MarkAiNotSynced(result.Error.Description);
        }
        else
        {
            property.MarkAiSyncFailed(result.Error.Description);
        }

        await SaveOutcomeAsync(cancellationToken);
    }

    // The property itself is already committed. If the bookkeeping cannot be saved because the row moved on
    // (archived meanwhile), the registry write the caller asked for still stands, so that is not an error here.
    private async Task SaveOutcomeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException exception)
            when (exception.Resource == PersistenceResource.Property)
        {
        }
    }
}
