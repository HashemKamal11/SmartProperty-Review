using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

public sealed class PropertyRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task AddTracksAndUnitOfWorkPersistsTheProperty()
    {
        var property = new Property(
            Guid.NewGuid(),
            PropertyType.Unit,
            new PropertyAddress(" sa ", latitude: 24.7136m),
            TestClock.DefaultNow);

        using (var scope = Host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await repository.AddAsync(property);
            Assert.Equal(1, await unitOfWork.SaveChangesAsync());
        }

        await using var verification = Host.CreateVerificationContext();
        var persisted = await verification.Properties.AsNoTracking().SingleAsync(row => row.Id == property.Id);

        Assert.Equal(PropertyType.Unit, persisted.Type);
        Assert.Equal(PropertyStatus.Active, persisted.Status);
        Assert.Equal("SA", persisted.Address.CountryCode);
        Assert.Equal(24.7136m, persisted.Address.Latitude);
        Assert.Null(persisted.Address.Longitude);
        Assert.Equal(TestClock.DefaultNow, persisted.CreatedAt);
        Assert.Equal(persisted.CreatedAt, persisted.UpdatedAt);
    }
}
