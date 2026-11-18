using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyImages;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

/// <summary>
/// Covers a property's photos against the real host, database and (temporary) disk storage: upload, order, delete,
/// the public file endpoint, and that the photos appear in the property itself.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PropertyImagesEndpointTests : IDisposable
{
    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public PropertyImagesEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    // ---- who may call

    [Fact]
    public async Task ChangingPhotosRequiresSigningIn()
    {
        using var client = _factory.CreateClient();
        var url = ImagesUrl(Guid.NewGuid());

        using var upload = await client.PostAsync(url, Form(("a.jpg", Jpeg())));
        using var order = await client.PutAsJsonAsync($"{url}/order", new ReorderPropertyImagesRequest([]));
        using var delete = await client.DeleteAsync($"{url}/{Guid.NewGuid()}");

        Assert.All([upload, order, delete], response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    [Fact]
    public async Task ChangingPhotosRequiresThePropertyUpdatePermission()
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());
        var url = ImagesUrl(property.Id);

        using var upload = await client.PostAsync(url, Form(("a.jpg", Jpeg())));
        using var order = await client.PutAsJsonAsync($"{url}/order", new ReorderPropertyImagesRequest([]));
        using var delete = await client.DeleteAsync($"{url}/{Guid.NewGuid()}");

        Assert.All([upload, order, delete], response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        Assert.Empty(Directory.Exists(_factory.ImageRoot) ? Directory.GetFiles(_factory.ImageRoot, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task AnApprovedOwnerCanManageThePhotosOfTheirOwnProperty()
    {
        var owner = await SeedUserAsync();
        var property = await SeedPropertyAsync(ownerUserId: owner);
        // An approved owner holds property.create and property.read only, never property.update.
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyCreate);
        using var client = _factory.CreateAuthenticatedClient(owner);
        var url = ImagesUrl(property.Id);

        using var upload = await client.PostAsync(url, Form(("a.jpg", Jpeg()), ("b.png", Png())));
        var images = (await upload.Content.ReadFromJsonAsync<PropertyImageResponse[]>())!;
        using var order = await client.PutAsJsonAsync($"{url}/order", new ReorderPropertyImagesRequest([images[1].Id, images[0].Id]));
        using var delete = await client.DeleteAsync($"{url}/{images[0].Id}");

        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task AnApprovedOwnerCannotChangeThePhotosOfAnotherOwnersProperty()
    {
        var owner = await SeedUserAsync();
        var stranger = await SeedUserAsync();
        var property = await SeedPropertyAsync(ownerUserId: owner);
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyCreate);
        using var client = _factory.CreateAuthenticatedClient(stranger);

        using var upload = await client.PostAsync(ImagesUrl(property.Id), Form(("a.jpg", Jpeg())));

        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Empty(StoredFiles());
    }

    // ---- upload

    [Fact]
    public async Task UploadingStoresTheFilesAndReturns201WithTheFirstAsCover()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();
        var jpeg = Jpeg(500);
        var png = Png(700);

        using var response = await client.PostAsync(ImagesUrl(property.Id), Form(("front.jpg", jpeg), ("plan.png", png)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyImageResponse[]>())!;
        Assert.Equal(2, body.Length);
        Assert.Equal(["front.jpg", "plan.png"], body.Select(image => image.FileName));
        Assert.Equal(["image/jpeg", "image/png"], body.Select(image => image.ContentType));
        Assert.Equal([500L, 700L], body.Select(image => image.SizeBytes));
        Assert.Equal([0, 1], body.Select(image => image.SortOrder));
        Assert.Equal([true, false], body.Select(image => image.IsCover));
        Assert.Equal($"/api/properties/{property.Id}/images/{body[0].Id}/file", body[0].Url);

        await using var context = _fixture.CreateContext();
        Assert.Equal(2, await context.PropertyImages.CountAsync(image => image.PropertyId == property.Id));
        var storedFiles = Directory.GetFiles(_factory.ImageRoot, "*", SearchOption.AllDirectories);
        Assert.Equal(2, storedFiles.Length);
        Assert.Contains(storedFiles, file => File.ReadAllBytes(file).SequenceEqual(jpeg));
    }

    [Fact]
    public async Task LaterUploadsGoAfterTheEarlierOnes()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();
        (await client.PostAsync(ImagesUrl(property.Id), Form(("a.jpg", Jpeg())))).Dispose();

        using var response = await client.PostAsync(ImagesUrl(property.Id), Form(("b.webp", WebP())));

        var added = (await response.Content.ReadFromJsonAsync<PropertyImageResponse[]>())!;
        Assert.Equal(1, Assert.Single(added).SortOrder);
        Assert.False(added[0].IsCover);
    }

    [Fact]
    public async Task AFileThatIsNotAnImageIs422WhateverItsNameAndNothingIsStored()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();

        using var response = await client.PostAsync(
            ImagesUrl(property.Id),
            Form(("good.jpg", Jpeg()), ("evil.jpg", "<html><script>alert(1)</script></html>"u8.ToArray())));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
        await using var context = _fixture.CreateContext();
        Assert.Equal(0, await context.PropertyImages.CountAsync(image => image.PropertyId == property.Id));
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task AFileOverFiveMegabytesIs422()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();

        using var response = await client.PostAsync(
            ImagesUrl(property.Id),
            Form(("big.jpg", Jpeg((int)PropertyImage.MaxSizeBytes + 1))));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task ARequestWithNoFilesIs422()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();

        // A well-formed form that carries no file: a rule is broken, so 422.
        using var form = new MultipartFormDataContent { { new StringContent("none"), "note" } };
        using var response = await client.PostAsync(ImagesUrl(property.Id), form);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task AMultipartBodyWithNoPartsAtAllIsMalformed()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();

        // Not a readable form at all, so it is refused before any rule is checked.
        using var response = await client.PostAsync(ImagesUrl(property.Id), new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task MoreThanTenFilesInOneRequestIs422()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();
        var files = Enumerable.Range(0, 11).Select(i => ($"{i}.jpg", Jpeg())).ToArray();

        using var response = await client.PostAsync(ImagesUrl(property.Id), Form(files));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task APropertyCannotHoldMoreThanTwentyImages()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();
        for (var batch = 0; batch < 2; batch++)
        {
            using var ok = await client.PostAsync(ImagesUrl(property.Id), Form(Enumerable.Range(0, 10).Select(i => ($"{i}.jpg", Jpeg())).ToArray()));
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        using var refused = await client.PostAsync(ImagesUrl(property.Id), Form(("one-more.jpg", Jpeg())));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        await using var context = _fixture.CreateContext();
        Assert.Equal(20, await context.PropertyImages.CountAsync(image => image.PropertyId == property.Id));
    }

    [Fact]
    public async Task UploadingToAnUnknownPropertyIs404()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsync(ImagesUrl(Guid.NewGuid()), Form(("a.jpg", Jpeg())));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("properties.not_found", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task UploadingToAnArchivedPropertyIs409()
    {
        var property = await SeedPropertyAsync(archive: true);
        using var client = AuthorizedClient();

        using var response = await client.PostAsync(ImagesUrl(property.Id), Form(("a.jpg", Jpeg())));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("properties.archived", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
        Assert.Empty(StoredFiles());
    }

    // ---- the file

    [Fact]
    public async Task ThePhotoIsServedToAnyoneWithItsTypeAndCachingHeaders()
    {
        var property = await SeedPropertyAsync();
        var bytes = Png(300);
        PropertyImageResponse uploaded;
        using (var client = AuthorizedClient())
        {
            using var upload = await client.PostAsync(ImagesUrl(property.Id), Form(("plan.png", bytes)));
            uploaded = (await upload.Content.ReadFromJsonAsync<PropertyImageResponse[]>())![0];
        }

        using var anonymous = _factory.CreateClient();
        using var response = await anonymous.GetAsync(uploaded.Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal($"\"{uploaded.Id:n}\"", response.Headers.ETag!.Tag);
        Assert.Contains("immutable", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task AClientThatAlreadyHasThePhotoGets304()
    {
        var property = await SeedPropertyAsync();
        PropertyImageResponse uploaded;
        using (var client = AuthorizedClient())
        {
            using var upload = await client.PostAsync(ImagesUrl(property.Id), Form(("a.jpg", Jpeg())));
            uploaded = (await upload.Content.ReadFromJsonAsync<PropertyImageResponse[]>())![0];
        }

        using var anonymous = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, uploaded.Url);
        request.Headers.TryAddWithoutValidation("If-None-Match", $"\"{uploaded.Id:n}\"");
        using var response = await anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }

    [Fact]
    public async Task APhotoOfAnotherPropertyOrAnUnknownOneIs404()
    {
        var property = await SeedPropertyAsync();
        var other = await SeedPropertyAsync();
        PropertyImageResponse uploaded;
        using (var client = AuthorizedClient())
        {
            using var upload = await client.PostAsync(ImagesUrl(property.Id), Form(("a.jpg", Jpeg())));
            uploaded = (await upload.Content.ReadFromJsonAsync<PropertyImageResponse[]>())![0];
        }

        using var anonymous = _factory.CreateClient();
        using var wrongProperty = await anonymous.GetAsync($"{ImagesUrl(other.Id)}/{uploaded.Id}/file");
        using var unknown = await anonymous.GetAsync($"{ImagesUrl(property.Id)}/{Guid.NewGuid()}/file");

        Assert.Equal(HttpStatusCode.NotFound, wrongProperty.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task ARowWhoseFileWasLostIs404NotAServerError()
    {
        var property = await SeedPropertyAsync();
        PropertyImageResponse uploaded;
        using (var client = AuthorizedClient())
        {
            using var upload = await client.PostAsync(ImagesUrl(property.Id), Form(("a.jpg", Jpeg())));
            uploaded = (await upload.Content.ReadFromJsonAsync<PropertyImageResponse[]>())![0];
        }

        foreach (var file in StoredFiles())
        {
            File.Delete(file);
        }

        using var anonymous = _factory.CreateClient();
        using var response = await anonymous.GetAsync(uploaded.Url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- order

    [Fact]
    public async Task ReorderingChangesThePositionsAndTheNewFirstIsTheCover()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();
        using var upload = await client.PostAsync(
            ImagesUrl(property.Id), Form(("a.jpg", Jpeg()), ("b.jpg", Jpeg()), ("c.jpg", Jpeg())));
        var images = (await upload.Content.ReadFromJsonAsync<PropertyImageResponse[]>())!;

        using var response = await client.PutAsJsonAsync(
            $"{ImagesUrl(property.Id)}/order",
            new ReorderPropertyImagesRequest([images[2].Id, images[0].Id, images[1].Id]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyImageResponse[]>())!;
        Assert.Equal([images[2].Id, images[0].Id, images[1].Id], body.Select(image => image.Id));
        Assert.Equal([true, false, false], body.Select(image => image.IsCover));
        await using var context = _fixture.CreateContext();
        var stored = await context.PropertyImages.AsNoTracking()
            .Where(image => image.PropertyId == property.Id)
            .OrderBy(image => image.SortOrder)
            .Select(image => image.Id)
            .ToListAsync();
        Assert.Equal([images[2].Id, images[0].Id, images[1].Id], stored);
    }

    [Fact]
    public async Task AnOrderThatDoesNotNameEveryImageOnceIs422()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();
        using var upload = await client.PostAsync(ImagesUrl(property.Id), Form(("a.jpg", Jpeg()), ("b.jpg", Jpeg())));
        var images = (await upload.Content.ReadFromJsonAsync<PropertyImageResponse[]>())!;

        using var missing = await client.PutAsJsonAsync(
            $"{ImagesUrl(property.Id)}/order", new ReorderPropertyImagesRequest([images[0].Id]));
        using var nothing = await client.PutAsJsonAsync(
            $"{ImagesUrl(property.Id)}/order", new ReorderPropertyImagesRequest(null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nothing.StatusCode);
    }

    [Fact]
    public async Task ReorderingAnUnknownPropertyIs404()
    {
        using var client = AuthorizedClient();

        using var response = await client.PutAsJsonAsync(
            $"{ImagesUrl(Guid.NewGuid())}/order", new ReorderPropertyImagesRequest([]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- delete

    [Fact]
    public async Task DeletingRemovesTheRowAndTheFileAndIsRepeatable()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();
        using var upload = await client.PostAsync(ImagesUrl(property.Id), Form(("a.jpg", Jpeg()), ("b.jpg", Jpeg())));
        var images = (await upload.Content.ReadFromJsonAsync<PropertyImageResponse[]>())!;

        using var first = await client.DeleteAsync($"{ImagesUrl(property.Id)}/{images[0].Id}");
        using var second = await client.DeleteAsync($"{ImagesUrl(property.Id)}/{images[0].Id}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        await using var context = _fixture.CreateContext();
        var remaining = await context.PropertyImages.AsNoTracking().SingleAsync(image => image.PropertyId == property.Id);
        Assert.Equal(images[1].Id, remaining.Id);
        Assert.Single(StoredFiles());
    }

    [Fact]
    public async Task DeletingFromAnUnknownPropertyIs404AndFromAnArchivedOneIs409()
    {
        var archived = await SeedPropertyAsync(archive: true);
        using var client = AuthorizedClient();

        using var unknown = await client.DeleteAsync($"{ImagesUrl(Guid.NewGuid())}/{Guid.NewGuid()}");
        using var frozen = await client.DeleteAsync($"{ImagesUrl(archived.Id)}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, frozen.StatusCode);
    }

    // ---- in the property

    [Fact]
    public async Task ThePropertyListsItsPhotosInOrderWithUrlsAndTheCover()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyRead);
        using var upload = await client.PostAsync(ImagesUrl(property.Id), Form(("a.jpg", Jpeg()), ("b.png", Png())));
        var images = (await upload.Content.ReadFromJsonAsync<PropertyImageResponse[]>())!;

        var read = (await client.GetFromJsonAsync<PropertyResponse>($"/api/properties/{property.Id}"))!;

        Assert.Equal(images.Select(image => image.Id), read.Images!.Select(image => image.Id));
        Assert.Equal(images.Select(image => image.Url), read.Images!.Select(image => image.Url));
        Assert.Equal([true, false], read.Images!.Select(image => image.IsCover));
    }

    [Fact]
    public async Task APropertyWithoutPhotosHasAnEmptyList()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyRead);

        var read = (await client.GetFromJsonAsync<PropertyResponse>($"/api/properties/{property.Id}"))!;

        Assert.NotNull(read.Images);
        Assert.Empty(read.Images!);
    }

    // ---- helpers

    private static string ImagesUrl(Guid propertyId) => $"/api/properties/{propertyId}/images";

    private HttpClient AuthorizedClient()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyUpdate);

        return _factory.CreateAuthenticatedClient(Guid.NewGuid());
    }

    private string[] StoredFiles()
    {
        return Directory.Exists(_factory.ImageRoot)
            ? Directory.GetFiles(_factory.ImageRoot, "*", SearchOption.AllDirectories)
            : [];
    }

    private static MultipartFormDataContent Form(params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();

        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "files", name);
        }

        return form;
    }

    private static byte[] Jpeg(int length = 64) => WithHeader([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01], length);

    private static byte[] Png(int length = 64) => WithHeader([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D], length);

    private static byte[] WebP(int length = 64) =>
        WithHeader("RIFF"u8.ToArray().Concat(new byte[] { 0x24, 0x00, 0x00, 0x00 }).Concat("WEBP"u8.ToArray()).ToArray(), length);

    private static byte[] WithHeader(byte[] header, int length)
    {
        var bytes = new byte[Math.Max(length, header.Length)];
        header.CopyTo(bytes, 0);

        for (var i = header.Length; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private async Task<Guid> SeedUserAsync()
    {
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Test", "Owner", DateTimeOffset.UtcNow);

        await using var context = _fixture.CreateContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }

    private async Task<Property> SeedPropertyAsync(bool archive = false, Guid? ownerUserId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var createdAt = new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero).AddMinutes(-10);
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), createdAt, ownerUserId: ownerUserId);

        if (archive)
        {
            property.Archive(createdAt.AddMinutes(1));
        }

        await using var context = _fixture.CreateContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property;
    }
}
