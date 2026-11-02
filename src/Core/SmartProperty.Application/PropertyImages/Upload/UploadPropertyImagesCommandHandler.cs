using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Storage;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyImages.Upload;

/// <summary>
/// Validates every file first, then stores them and records them in order after the property's current last image.
/// </summary>
/// <remarks>
/// Nothing is stored until every file has passed, so a refused upload leaves no trace. If recording the images fails
/// after the files were written, those files are deleted again rather than left behind with nothing pointing at them.
/// </remarks>
public sealed class UploadPropertyImagesCommandHandler(
    IPropertyRepository propertyRepository,
    IPropertyImageRepository imageRepository,
    IImageStorage imageStorage,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<UploadPropertyImagesCommand, IReadOnlyList<PropertyImageDetails>>
{
    /// <summary>How many files one request may carry, so a single request cannot pin the server for long.</summary>
    public const int MaxFilesPerRequest = 10;

    public async Task<Result<IReadOnlyList<PropertyImageDetails>>> Handle(
        UploadPropertyImagesCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Files is null || command.Files.Count == 0)
        {
            return Failure(PropertyImageErrors.ValidationFailed("At least one image file is required."));
        }

        if (command.Files.Count > MaxFilesPerRequest)
        {
            return Failure(PropertyImageErrors.ValidationFailed(
                $"A request may carry at most {MaxFilesPerRequest} files."));
        }

        if (command.PropertyId == Guid.Empty)
        {
            return Failure(PropertyImageErrors.PropertyNotFound);
        }

        var property = await propertyRepository.GetDetailsByIdAsync(command.PropertyId, cancellationToken);

        if (property is null)
        {
            return Failure(PropertyImageErrors.PropertyNotFound);
        }

        if (property.Status != PropertyStatus.Active)
        {
            return Failure(PropertyImageErrors.PropertyArchived);
        }

        var existing = await imageRepository.CountAsync(command.PropertyId, cancellationToken);
        if (existing + command.Files.Count > PropertyImage.MaxPerProperty)
        {
            return Failure(PropertyImageErrors.ValidationFailed(
                $"A property may have at most {PropertyImage.MaxPerProperty} images; it has {existing}."));
        }

        // Every file is checked before any is stored.
        var checkedFiles = new List<(ImageUpload File, string ContentType)>(command.Files.Count);

        foreach (var file in command.Files)
        {
            var label = string.IsNullOrWhiteSpace(file.FileName) ? "A file" : $"'{file.FileName}'";

            if (!file.Content.CanRead || !file.Content.CanSeek)
            {
                return Failure(PropertyImageErrors.ValidationFailed($"{label} could not be read."));
            }

            if (file.Length <= 0)
            {
                return Failure(PropertyImageErrors.ValidationFailed($"{label} is empty."));
            }

            if (file.Length > PropertyImage.MaxSizeBytes)
            {
                return Failure(PropertyImageErrors.ValidationFailed(
                    $"{label} is larger than {PropertyImage.MaxSizeBytes / (1024 * 1024)} MB."));
            }

            var header = new byte[ImageContentSniffer.HeaderLength];
            file.Content.Seek(0, SeekOrigin.Begin);
            var read = await ReadAtLeastAsync(file.Content, header, cancellationToken);

            var contentType = ImageContentSniffer.Detect(header.AsSpan(0, read));
            if (contentType is null)
            {
                return Failure(PropertyImageErrors.ValidationFailed(
                    $"{label} is not a supported image. Only JPEG, PNG, and WebP files are accepted."));
            }

            checkedFiles.Add((file, contentType));
        }

        var createdAt = dateTimeProvider.UtcNow;
        var nextOrder = await imageRepository.NextSortOrderAsync(command.PropertyId, cancellationToken);
        var stored = new List<string>(checkedFiles.Count);
        var images = new List<PropertyImage>(checkedFiles.Count);

        try
        {
            foreach (var (file, contentType) in checkedFiles)
            {
                var id = Guid.NewGuid();
                var key = $"properties/{command.PropertyId:N}/{id:N}{PropertyImage.ExtensionOf(contentType)}";

                file.Content.Seek(0, SeekOrigin.Begin);
                await imageStorage.SaveAsync(key, file.Content, cancellationToken);
                stored.Add(key);

                var image = new PropertyImage(
                    id, command.PropertyId, key, file.FileName, contentType, file.Length, nextOrder++, createdAt);
                images.Add(image);
                await imageRepository.AddAsync(image, cancellationToken);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // The files are written but never recorded: delete them so nothing is orphaned, then report the failure.
            foreach (var key in stored)
            {
                await DeleteQuietlyAsync(key);
            }

            throw;
        }

        return Result<IReadOnlyList<PropertyImageDetails>>.Success(
            images.Select(image => new PropertyImageDetails(
                image.Id, image.FileName, image.ContentType, image.SizeBytes, image.SortOrder)).ToList());
    }

    private async Task DeleteQuietlyAsync(string key)
    {
        try
        {
            await imageStorage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the original failure is the one worth reporting.
        }
    }

    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static Result<IReadOnlyList<PropertyImageDetails>> Failure(Error error)
    {
        return Result<IReadOnlyList<PropertyImageDetails>>.Failure(error);
    }
}
