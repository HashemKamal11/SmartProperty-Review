using SmartProperty.Application.PropertyImages.Delete;
using SmartProperty.Application.PropertyImages.GetFile;
using SmartProperty.Application.PropertyImages.Reorder;
using SmartProperty.Application.PropertyImages.Upload;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyImages;

public sealed class PropertyImageHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    private readonly FakePropertyRepository _properties = new();
    private readonly FakePropertyImageRepository _images = new();
    private readonly FakeImageStorage _storage = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly Guid _propertyId;

    public PropertyImageHandlerTests()
    {
        _propertyId = SeedProperty();
    }

    // ---- upload

    [Fact]
    public async Task UploadStoresEachFileRecordsItInOrderAndCommitsOnce()
    {
        var result = await UploadHandler().Handle(Upload(("front.jpg", TestImages.Jpeg(100)), ("plan.png", TestImages.Png(200))));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal([0, 1], result.Value.Select(image => image.SortOrder));
        Assert.Equal(["front.jpg", "plan.png"], result.Value.Select(image => image.FileName));
        Assert.Equal(["image/jpeg", "image/png"], result.Value.Select(image => image.ContentType));
        Assert.Equal([100L, 200L], result.Value.Select(image => image.SizeBytes));

        Assert.Equal(2, _storage.Saved.Count);
        Assert.Equal(2, _images.Added.Count);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task TheStoredBytesAreTheUploadedBytes()
    {
        var bytes = TestImages.WebP(300);

        var result = await UploadHandler().Handle(Upload(("a.webp", bytes)));

        var key = Assert.Single(_images.Added).StorageKey;
        Assert.Equal(bytes, _storage.Files[key]);
        Assert.Equal("image/webp", result.Value[0].ContentType);
    }

    [Fact]
    public async Task TheStorageKeyIsGeneratedFromTheIdsAndNeverFromTheClientsFileName()
    {
        await UploadHandler().Handle(Upload(("../../etc/passwd.jpg", TestImages.Jpeg())));

        var image = Assert.Single(_images.Added);
        Assert.Equal($"properties/{_propertyId:N}/{image.Id:N}.jpg", image.StorageKey);
        Assert.DoesNotContain("passwd", image.StorageKey);
        Assert.Equal("passwd.jpg", image.FileName);
    }

    [Fact]
    public async Task TheTypeComesFromTheBytesNotFromTheFileNameExtension()
    {
        var result = await UploadHandler().Handle(Upload(("looks-like-a-png.png", TestImages.Jpeg())));

        Assert.True(result.IsSuccess);
        Assert.Equal("image/jpeg", result.Value[0].ContentType);
        Assert.EndsWith(".jpg", Assert.Single(_images.Added).StorageKey);
    }

    [Fact]
    public async Task NewImagesGoAfterTheOnesTheProperyAlreadyHas()
    {
        SeedImage(sortOrder: 0);
        SeedImage(sortOrder: 4);

        var result = await UploadHandler().Handle(Upload(("a.jpg", TestImages.Jpeg()), ("b.jpg", TestImages.Jpeg())));

        Assert.Equal([5, 6], result.Value.Select(image => image.SortOrder));
    }

    [Theory]
    [InlineData("script.jpg")]
    [InlineData("page.png")]
    public async Task AFileThatIsNotAnImageIsRefusedWhateverItsNameSays(string name)
    {
        var result = await UploadHandler().Handle(Upload((name, TestImages.Html())));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Contains(name, result.Error.Description);
        Assert.Contains("JPEG, PNG, and WebP", result.Error.Description);
        Assert.Empty(_storage.Saved);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task AGifIsRefused()
    {
        var result = await UploadHandler().Handle(Upload(("anim.gif", TestImages.Gif())));

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task OneBadFileRefusesTheWholeUploadAndStoresNothing()
    {
        var result = await UploadHandler().Handle(Upload(("good.jpg", TestImages.Jpeg()), ("bad.txt", TestImages.Html())));

        Assert.True(result.IsFailure);
        Assert.Empty(_storage.Saved);
        Assert.Empty(_images.Added);
    }

    [Fact]
    public async Task AnEmptyFileIsRefused()
    {
        var result = await UploadHandler().Handle(Upload(("empty.jpg", [])));

        Assert.True(result.IsFailure);
        Assert.Contains("empty", result.Error!.Description);
    }

    [Fact]
    public async Task AFileOverTheSizeLimitIsRefusedAndOneAtTheLimitIsAccepted()
    {
        var over = new UploadPropertyImagesCommand(
            _propertyId,
            [new ImageUpload("big.jpg", new MemoryStream(TestImages.Jpeg(64)), PropertyImage.MaxSizeBytes + 1)]);
        var atLimit = new UploadPropertyImagesCommand(
            _propertyId,
            [new ImageUpload("limit.jpg", new MemoryStream(TestImages.Jpeg(64)), PropertyImage.MaxSizeBytes)]);

        var refused = await UploadHandler().Handle(over);
        var accepted = await UploadHandler().Handle(atLimit);

        Assert.True(refused.IsFailure);
        Assert.Contains("5 MB", refused.Error!.Description);
        Assert.True(accepted.IsSuccess);
    }

    [Fact]
    public async Task NoFilesIsRefused()
    {
        var result = await UploadHandler().Handle(new UploadPropertyImagesCommand(_propertyId, []));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Fact]
    public async Task MoreThanTenFilesInOneRequestIsRefused()
    {
        var files = Enumerable.Range(0, UploadPropertyImagesCommandHandler.MaxFilesPerRequest + 1)
            .Select(i => ($"{i}.jpg", TestImages.Jpeg()))
            .ToArray();

        var result = await UploadHandler().Handle(Upload(files));

        Assert.True(result.IsFailure);
        Assert.Contains("10", result.Error!.Description);
        Assert.Empty(_storage.Saved);
    }

    [Fact]
    public async Task ThePropertyMayHoldAtMostTwentyImagesInTotal()
    {
        for (var i = 0; i < PropertyImage.MaxPerProperty - 1; i++)
        {
            SeedImage(sortOrder: i);
        }

        var one = await UploadHandler().Handle(Upload(("last.jpg", TestImages.Jpeg())));
        var twoMore = await UploadHandler().Handle(Upload(("x.jpg", TestImages.Jpeg()), ("y.jpg", TestImages.Jpeg())));

        Assert.True(one.IsSuccess);
        Assert.True(twoMore.IsFailure);
        Assert.Contains("20", twoMore.Error!.Description);
    }

    [Fact]
    public async Task AnUnknownPropertyIsNotFoundAndNothingIsStored()
    {
        var result = await UploadHandler().Handle(
            new UploadPropertyImagesCommand(Guid.NewGuid(), [new ImageUpload("a.jpg", new MemoryStream(TestImages.Jpeg()), 64)]));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.not_found", result.Error!.Code);
        Assert.Empty(_storage.Saved);
    }

    [Fact]
    public async Task AnArchivedPropertyCannotGainImages()
    {
        var archived = SeedProperty(PropertyStatus.Archived);

        var result = await UploadHandler().Handle(
            new UploadPropertyImagesCommand(archived, [new ImageUpload("a.jpg", new MemoryStream(TestImages.Jpeg()), 64)]));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.archived", result.Error!.Code);
        Assert.Empty(_storage.Saved);
    }

    [Fact]
    public async Task AFileFailingToSaveLeavesNoOrphanFilesBehind()
    {
        _storage.FailOnSaveNumber = 1;

        await Assert.ThrowsAsync<IOException>(() =>
            UploadHandler().Handle(Upload(("a.jpg", TestImages.Jpeg()), ("b.jpg", TestImages.Jpeg()), ("c.jpg", TestImages.Jpeg()))));

        Assert.Empty(_storage.Files);
        Assert.Single(_storage.Deleted);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task AFailedCommitDeletesTheFilesItHadWritten()
    {
        _unitOfWork.ExceptionToThrow = new InvalidOperationException("database down");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UploadHandler().Handle(Upload(("a.jpg", TestImages.Jpeg()), ("b.png", TestImages.Png()))));

        Assert.Empty(_storage.Files);
        Assert.Equal(2, _storage.Deleted.Count);
    }

    [Fact]
    public async Task AStreamThatCannotBeSoughtIsRefusedRatherThanReadWrongly()
    {
        var result = await UploadHandler().Handle(new UploadPropertyImagesCommand(
            _propertyId,
            [new ImageUpload("a.jpg", new NonSeekableStream(TestImages.Jpeg()), 64)]));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
    }

    // ---- delete

    [Fact]
    public async Task DeleteRemovesTheRowThenTheFile()
    {
        var image = SeedImage(sortOrder: 0);
        _storage.Files[image.StorageKey] = [1, 2, 3];

        var result = await DeleteHandler().Handle(new DeletePropertyImageCommand(_propertyId, image.Id));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Removed);
        Assert.Empty(_images.Stored);
        Assert.Equal([image.StorageKey], _storage.Deleted);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task DeletingAnImageThePropertyDoesNotHaveIsASuccessThatChangesNothing()
    {
        var result = await DeleteHandler().Handle(new DeletePropertyImageCommand(_propertyId, Guid.NewGuid()));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Removed);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Empty(_storage.Deleted);
    }

    [Fact]
    public async Task DeleteNeverReachesAnotherPropertysImage()
    {
        var other = SeedProperty();
        var image = SeedImage(sortOrder: 0, propertyId: other);

        var result = await DeleteHandler().Handle(new DeletePropertyImageCommand(_propertyId, image.Id));

        Assert.False(result.Value.Removed);
        Assert.Single(_images.Stored);
    }

    [Fact]
    public async Task AFileThatCannotBeDeletedStillLeavesTheImageRemovedFromTheProperty()
    {
        var image = SeedImage(sortOrder: 0);
        _storage.FailOnDelete = true;

        var result = await DeleteHandler().Handle(new DeletePropertyImageCommand(_propertyId, image.Id));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Removed);
        Assert.Empty(_images.Stored);
    }

    [Fact]
    public async Task DeleteRefusesAnUnknownAndAnArchivedProperty()
    {
        var archived = SeedProperty(PropertyStatus.Archived);

        var unknown = await DeleteHandler().Handle(new DeletePropertyImageCommand(Guid.NewGuid(), Guid.NewGuid()));
        var frozen = await DeleteHandler().Handle(new DeletePropertyImageCommand(archived, Guid.NewGuid()));

        Assert.Equal("properties.not_found", unknown.Error!.Code);
        Assert.Equal("properties.archived", frozen.Error!.Code);
    }

    // ---- reorder

    [Fact]
    public async Task ReorderSetsThePositionsAndTheFirstBecomesTheCover()
    {
        var a = SeedImage(sortOrder: 0);
        var b = SeedImage(sortOrder: 1);
        var c = SeedImage(sortOrder: 2);

        var result = await ReorderHandler().Handle(new ReorderPropertyImagesCommand(_propertyId, [c.Id, a.Id, b.Id]));

        Assert.True(result.IsSuccess);
        Assert.Equal([c.Id, a.Id, b.Id], result.Value.Select(image => image.Id));
        Assert.Equal([0, 1, 2], result.Value.Select(image => image.SortOrder));
        Assert.Equal(0, c.SortOrder);
        Assert.Equal(1, a.SortOrder);
        Assert.Equal(2, b.SortOrder);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ReorderingToTheCurrentOrderCommitsNothing()
    {
        var a = SeedImage(sortOrder: 0);
        var b = SeedImage(sortOrder: 1);

        var result = await ReorderHandler().Handle(new ReorderPropertyImagesCommand(_propertyId, [a.Id, b.Id]));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ReorderClosesGapsLeftByDeletes()
    {
        var a = SeedImage(sortOrder: 3);
        var b = SeedImage(sortOrder: 9);

        var result = await ReorderHandler().Handle(new ReorderPropertyImagesCommand(_propertyId, [a.Id, b.Id]));

        Assert.Equal([0, 1], result.Value.Select(image => image.SortOrder));
    }

    [Fact]
    public async Task ReorderMustNameEveryImageExactlyOnce()
    {
        var a = SeedImage(sortOrder: 0);
        var b = SeedImage(sortOrder: 1);

        var missing = await ReorderHandler().Handle(new ReorderPropertyImagesCommand(_propertyId, [a.Id]));
        var extra = await ReorderHandler().Handle(new ReorderPropertyImagesCommand(_propertyId, [a.Id, b.Id, Guid.NewGuid()]));
        var repeated = await ReorderHandler().Handle(new ReorderPropertyImagesCommand(_propertyId, [a.Id, a.Id]));
        var none = await ReorderHandler().Handle(new ReorderPropertyImagesCommand(_propertyId, null));

        Assert.All([missing, extra, repeated, none], result =>
        {
            Assert.True(result.IsFailure);
            Assert.Equal("validation.failed", result.Error!.Code);
        });
        Assert.Equal(0, a.SortOrder);
        Assert.Equal(1, b.SortOrder);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ReorderRefusesAnImageOfAnotherProperty()
    {
        var mine = SeedImage(sortOrder: 0);
        var theirs = SeedImage(sortOrder: 0, propertyId: SeedProperty());

        var result = await ReorderHandler().Handle(new ReorderPropertyImagesCommand(_propertyId, [mine.Id, theirs.Id]));

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task ReorderRefusesAnUnknownAndAnArchivedProperty()
    {
        var archived = SeedProperty(PropertyStatus.Archived);

        var unknown = await ReorderHandler().Handle(new ReorderPropertyImagesCommand(Guid.NewGuid(), []));
        var frozen = await ReorderHandler().Handle(new ReorderPropertyImagesCommand(archived, []));

        Assert.Equal("properties.not_found", unknown.Error!.Code);
        Assert.Equal("properties.archived", frozen.Error!.Code);
    }

    // ---- file

    [Fact]
    public async Task TheFileOfAnImageIsOpenedWithItsTypeAndName()
    {
        var image = SeedImage(sortOrder: 0);
        var bytes = TestImages.Jpeg(50);
        _storage.Files[image.StorageKey] = bytes;

        var result = await FileHandler().Handle(new GetPropertyImageFileQuery(_propertyId, image.Id));

        Assert.True(result.IsSuccess);
        await using var content = result.Value.Content;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
        Assert.Equal("image/jpeg", result.Value.ContentType);
        Assert.Equal(image.FileName, result.Value.FileName);
    }

    [Fact]
    public async Task AnImageIsOnlyOpenedUnderItsOwnProperty()
    {
        var image = SeedImage(sortOrder: 0);
        _storage.Files[image.StorageKey] = [1];

        var result = await FileHandler().Handle(new GetPropertyImageFileQuery(SeedProperty(), image.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("property_images.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task ARowWhoseFileIsMissingIsNotFoundRatherThanAServerError()
    {
        var image = SeedImage(sortOrder: 0);

        var result = await FileHandler().Handle(new GetPropertyImageFileQuery(_propertyId, image.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("property_images.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task EmptyIdsAreNotFound()
    {
        var empty = await FileHandler().Handle(new GetPropertyImageFileQuery(Guid.Empty, Guid.NewGuid()));
        var emptyImage = await FileHandler().Handle(new GetPropertyImageFileQuery(_propertyId, Guid.Empty));

        Assert.True(empty.IsFailure);
        Assert.True(emptyImage.IsFailure);
    }

    // ---- helpers

    private Guid SeedProperty(PropertyStatus status = PropertyStatus.Active)
    {
        var id = Guid.NewGuid();
        _properties.Details[id] = new PropertyDetails(
            id,
            PropertyType.Apartment,
            status,
            new PropertyAddressDetails("EG", null, null, null, null, null, null, null),
            Now.AddDays(-5),
            Now.AddDays(-5));

        return id;
    }

    private PropertyImage SeedImage(int sortOrder, Guid? propertyId = null)
    {
        var owner = propertyId ?? _propertyId;
        var id = Guid.NewGuid();
        var image = new PropertyImage(
            id, owner, $"properties/{owner:N}/{id:N}.jpg", "seed.jpg", PropertyImage.Jpeg, 100, sortOrder, Now.AddMinutes(sortOrder));
        _images.Stored.Add(image);

        return image;
    }

    private UploadPropertyImagesCommand Upload(params (string Name, byte[] Bytes)[] files)
    {
        return new UploadPropertyImagesCommand(
            _propertyId,
            files.Select(file => new ImageUpload(file.Name, new MemoryStream(file.Bytes), file.Bytes.Length)).ToList());
    }

    private UploadPropertyImagesCommandHandler UploadHandler()
    {
        return new UploadPropertyImagesCommandHandler(
            _properties, _images, _storage, new FakeDateTimeProvider(Now), _unitOfWork);
    }

    private DeletePropertyImageCommandHandler DeleteHandler()
    {
        return new DeletePropertyImageCommandHandler(_properties, _images, _storage, _unitOfWork);
    }

    private ReorderPropertyImagesCommandHandler ReorderHandler()
    {
        return new ReorderPropertyImagesCommandHandler(_properties, _images, _unitOfWork);
    }

    private GetPropertyImageFileQueryHandler FileHandler()
    {
        return new GetPropertyImageFileQueryHandler(_images, _storage);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
