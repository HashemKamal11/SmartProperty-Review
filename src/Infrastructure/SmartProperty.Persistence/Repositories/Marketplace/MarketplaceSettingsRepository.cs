using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Marketplace;

/// <summary>The one-row marketplace settings table. When no administrator has stored a value, the defaults apply.</summary>
internal sealed class MarketplaceSettingsRepository(ApplicationDbContext context) : IMarketplaceSettingsRepository
{
    public async Task<int> GetSigningWindowHoursAsync(CancellationToken cancellationToken = default)
    {
        var stored = await context.MarketplaceSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);

        return stored?.SigningWindowHours ?? MarketplaceSettings.DefaultSigningWindowHours;
    }

    public async Task<MarketplaceSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        return await context.MarketplaceSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
    }

    public async Task SaveSigningWindowAsync(
        int signingWindowHours,
        Guid administratorUserId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var stored = await context.MarketplaceSettings.SingleOrDefaultAsync(cancellationToken);

        if (stored is null)
        {
            context.MarketplaceSettings.Add(new MarketplaceSettings(signingWindowHours, administratorUserId, at));
        }
        else
        {
            stored.ChangeSigningWindow(signingWindowHours, administratorUserId, at);
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
