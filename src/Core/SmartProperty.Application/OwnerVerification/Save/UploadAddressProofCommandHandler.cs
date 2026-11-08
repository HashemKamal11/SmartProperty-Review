using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Storage;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.PropertyImages;
using SmartProperty.Common.Results;
using SmartProperty.Domain.OwnerVerification;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.OwnerVerification.Save;

/// <summary>Stores the national-address proof image of the signed-in user, replacing any earlier one.</summary>
public sealed record UploadAddressProofCommand(Guid UserId, string? FileName, Stream Content, long Length)
    : ICommand<OwnerProfileDetails>;

public sealed class UploadAddressProofCommandHandler(
    IUserRepository userRepository,
    IOwnerProfileRepository profileRepository,
    OwnerAccessProvisioner accessProvisioner,
    IImageStorage imageStorage,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<UploadAddressProofCommand, OwnerProfileDetails>
{
    public const long MaxSizeBytes = 10L * 1024 * 1024;

    public async Task<Result<OwnerProfileDetails>> Handle(
        UploadAddressProofCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Content is null || !command.Content.CanRead || !command.Content.CanSeek)
        {
            return Failure(OwnerVerificationErrors.ValidationFailed("The file could not be read."));
        }

        if (command.Length <= 0)
        {
            return Failure(OwnerVerificationErrors.ValidationFailed("The file is empty."));
        }

        if (command.Length > MaxSizeBytes)
        {
            return Failure(OwnerVerificationErrors.ValidationFailed(
                $"The file is larger than {MaxSizeBytes / (1024 * 1024)} MB."));
        }

        var header = new byte[ImageContentSniffer.HeaderLength];
        command.Content.Seek(0, SeekOrigin.Begin);
        var read = await ReadAtLeastAsync(command.Content, header, cancellationToken);

        var contentType = ImageContentSniffer.Detect(header.AsSpan(0, read));

        if (contentType is null)
        {
            return Failure(OwnerVerificationErrors.ValidationFailed(
                "The file is not a supported image. Only JPEG, PNG, and WebP files are accepted."));
        }

        var now = dateTimeProvider.UtcNow;

        var loaded = await OwnerProfileAccess.LoadEditableAsync(
            command.UserId,
            userRepository,
            profileRepository,
            () => new OwnerProfile(Guid.NewGuid(), command.UserId, now),
            cancellationToken);

        if (loaded.IsFailure)
        {
            return Failure(loaded.Error!);
        }

        var (user, profile) = loaded.Value;
        var previousKey = profile.AddressProofKey;
        var key = $"owner-profiles/{profile.Id:N}/address-proof-{Guid.NewGuid():N}{PropertyImage.ExtensionOf(contentType)}";

        command.Content.Seek(0, SeekOrigin.Begin);
        await imageStorage.SaveAsync(key, command.Content, cancellationToken);

        try
        {
            var wasApproved = profile.SetAddressProof(key, contentType, command.FileName, now);

            if (wasApproved)
            {
                await accessProvisioner.RevokeAsync(command.UserId, cancellationToken);
            }

            if (await OwnerProfileAccess.SaveAsync(unitOfWork, cancellationToken) is { } error)
            {
                await DeleteQuietlyAsync(key);

                return Failure(error);
            }
        }
        catch
        {
            // The file is written but never recorded: delete it so nothing is orphaned, then report the failure.
            await DeleteQuietlyAsync(key);

            throw;
        }

        if (previousKey is not null)
        {
            await DeleteQuietlyAsync(previousKey);
        }

        return Result<OwnerProfileDetails>.Success(OwnerProfileDetails.From(user, profile));
    }

    private async Task DeleteQuietlyAsync(string key)
    {
        try
        {
            await imageStorage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the original outcome is the one worth reporting.
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

    private static Result<OwnerProfileDetails> Failure(Error error)
    {
        return Result<OwnerProfileDetails>.Failure(error);
    }
}
