using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>
/// Proves a property's photos survive real PostgreSQL: the table and its rules, the repository, and that the property
/// read model carries the photos in order.
/// </summary>
public sealed class PropertyImagePersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset CreatedAt = TestClock.DefaultNow;

    // ---- the table

    [Fact]
    public async Task AnImageRoundTrips()
    {
        var propertyId = await SeedPropertyAsync();
        var image = NewImage(propertyId, sortOrder: 3, fileName: "صورة.jpg");

        await using (var writing = Host.CreateVerificationContext())
        {
            writing.PropertyImages.Add(image);
            await writing.SaveChangesAsync();
        }

        await using var reading = Host.CreateVerificationContext();
        var reloaded = await reading.PropertyImages.AsNoTracking().SingleAsync();
        Assert.Equal(image.Id, reloaded.Id);
        Assert.Equal(propertyId, reloaded.PropertyId);
        Assert.Equal(image.StorageKey, reloaded.StorageKey);
        Assert.Equal("صورة.jpg", reloaded.FileName);
        Assert.Equal(PropertyImage.Jpeg, reloaded.ContentType);
        Assert.Equal(1000, reloaded.SizeBytes);
        Assert.Equal(3, reloaded.SortOrder);
        Assert.Equal(CreatedAt, reloaded.CreatedAt);
    }

    [Fact]
    public async Task TwoImagesCannotShareAStorageKey()
    {
        var propertyId = await SeedPropertyAsync();
        var first = NewImage(propertyId, 0);
        await SaveAsync(first);
        var duplicate = new PropertyImage(
            Guid.NewGuid(), propertyId, first.StorageKey, "b.jpg", PropertyImage.Jpeg, 10, 1, CreatedAt);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(duplicate));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("ux_registry_property_images_storage_key", postgres.ConstraintName);
    }

    [Fact]
    public async Task AnImageNeedsARealProperty()
    {
        var orphan = NewImage(Guid.NewGuid(), 0);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(orphan));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)exception.InnerException!).SqlState);
    }

    [Fact]
    public async Task APropertyThatHasImagesCannotBeDeleted()
    {
        var propertyId = await SeedPropertyAsync();
        await SaveAsync(NewImage(propertyId, 0));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"DELETE FROM registry.properties WHERE id = '{propertyId}';"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Theory]
    [InlineData("'image/gif'", "100", "0", "ck_registry_property_images_content_type")]
    [InlineData("'image/jpeg'", "0", "0", "ck_registry_property_images_size")]
    [InlineData("'image/jpeg'", "5242881", "0", "ck_registry_property_images_size")]
    [InlineData("'image/jpeg'", "100", "-1", "ck_registry_property_images_sort_order")]
    public async Task TheDatabaseRefusesWhatTheDomainRefuses(string contentType, string size, string sortOrder, string constraint)
    {
        var propertyId = await SeedPropertyAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "INSERT INTO registry.property_images " +
            "(id, property_id, storage_key, file_name, content_type, size_bytes, sort_order, created_at) VALUES " +
            $"('{Guid.NewGuid()}', '{propertyId}', 'k/{Guid.NewGuid():N}', 'a.jpg', {contentType}, {size}, {sortOrder}, now());"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    // ---- the repository

    [Fact]
    public async Task ListReturnsOnlyThePropertysImagesInOrder()
    {
        var propertyId = await SeedPropertyAsync();
        var other = await SeedPropertyAsync();
        var third = NewImage(propertyId, 2);
        var first = NewImage(propertyId, 0);
        var second = NewImage(propertyId, 1);
        await SaveAsync(third, first, second, NewImage(other, 0));
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyImageRepository>();

        var listed = await repository.ListAsync(propertyId);

        Assert.Equal([first.Id, second.Id, third.Id], listed.Select(image => image.Id));
    }

    [Fact]
    public async Task CountAndNextSortOrderFollowTheImagesTheProperyHas()
    {
        var propertyId = await SeedPropertyAsync();
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyImageRepository>();

        Assert.Equal(0, await repository.CountAsync(propertyId));
        Assert.Equal(0, await repository.NextSortOrderAsync(propertyId));

        await SaveAsync(NewImage(propertyId, 0), NewImage(propertyId, 6));

        Assert.Equal(2, await repository.CountAsync(propertyId));
        Assert.Equal(7, await repository.NextSortOrderAsync(propertyId));
    }

    [Fact]
    public async Task AddThenSaveStoresTheImageAndRemoveThenSaveReturnsItsKey()
    {
        var propertyId = await SeedPropertyAsync();
        var image = NewImage(propertyId, 0);
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyImageRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await repository.AddAsync(image);
        await unitOfWork.SaveChangesAsync();
        Assert.NotNull(await repository.FindAsync(propertyId, image.Id));

        var key = await repository.RemoveAsync(propertyId, image.Id);
        await unitOfWork.SaveChangesAsync();

        Assert.Equal(image.StorageKey, key);
        Assert.Null(await repository.FindAsync(propertyId, image.Id));
    }

    [Fact]
    public async Task FindAndRemoveNeverReachAnotherPropertysImage()
    {
        var propertyId = await SeedPropertyAsync();
        var other = await SeedPropertyAsync();
        var image = NewImage(other, 0);
        await SaveAsync(image);
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyImageRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Assert.Null(await repository.FindAsync(propertyId, image.Id));
        Assert.Null(await repository.RemoveAsync(propertyId, image.Id));
        await unitOfWork.SaveChangesAsync();
        Assert.NotNull(await repository.FindAsync(other, image.Id));
    }

    [Fact]
    public async Task ReorderingTrackedImagesIsSavedWithOneCommit()
    {
        var propertyId = await SeedPropertyAsync();
        var a = NewImage(propertyId, 0);
        var b = NewImage(propertyId, 1);
        await SaveAsync(a, b);
        using (var scope = Host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPropertyImageRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var tracked = await repository.ListAsync(propertyId);
            tracked.Single(image => image.Id == a.Id).MoveTo(1);
            tracked.Single(image => image.Id == b.Id).MoveTo(0);
            await unitOfWork.SaveChangesAsync();
        }

        using var reading = Host.CreateScope();
        var listed = await reading.ServiceProvider.GetRequiredService<IPropertyImageRepository>().ListAsync(propertyId);

        Assert.Equal([b.Id, a.Id], listed.Select(image => image.Id));
    }

    // ---- the property read model

    [Fact]
    public async Task GetDetailsCarriesTheImagesInOrderWithoutAnExtraQuery()
    {
        var propertyId = await SeedPropertyAsync();
        var second = NewImage(propertyId, 1);
        var first = NewImage(propertyId, 0);
        await SaveAsync(second, first);
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        var details = await repository.GetDetailsByIdAsync(propertyId);

        Assert.Equal([first.Id, second.Id], details!.Images!.Select(image => image.Id));
        Assert.Equal(PropertyImage.Jpeg, details.Images![0].ContentType);
        Assert.Equal(1000, details.Images[0].SizeBytes);
    }

    [Fact]
    public async Task APropertyWithoutImagesHasAnEmptyList()
    {
        var propertyId = await SeedPropertyAsync();
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        var details = await repository.GetDetailsByIdAsync(propertyId);

        Assert.NotNull(details!.Images);
        Assert.Empty(details.Images!);
    }

    [Fact]
    public async Task LoadingAPropertyForUpdateIncludesItsImages()
    {
        var propertyId = await SeedPropertyAsync();
        await SaveAsync(NewImage(propertyId, 0), NewImage(propertyId, 1));
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        var property = await repository.GetByIdAsync(propertyId);

        Assert.Equal(2, property!.Images.Count);
    }

    // ---- helpers

    private static PropertyImage NewImage(Guid propertyId, int sortOrder, string fileName = "a.jpg")
    {
        var id = Guid.NewGuid();

        return new PropertyImage(
            id, propertyId, $"properties/{propertyId:N}/{id:N}.jpg", fileName, PropertyImage.Jpeg, 1000, sortOrder, CreatedAt);
    }

    private async Task<Guid> SeedPropertyAsync()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), CreatedAt);

        await using var context = Host.CreateVerificationContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property.Id;
    }

    private async Task SaveAsync(params PropertyImage[] images)
    {
        await using var context = Host.CreateVerificationContext();
        context.PropertyImages.AddRange(images);
        await context.SaveChangesAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
