namespace SmartProperty.Domain.Workspaces;

/// <summary>
/// A company's request for its own workspace. The applicant is an ordinary account; a platform administrator
/// approves the request, which creates the workspace and makes the applicant its admin, or rejects it with a reason.
/// A decision is final.
/// </summary>
public sealed class WorkspaceRegistrationRequest
{
    public const int CompanyNameMaxLength = 200;
    public const int CommercialRegistrationNumberMaxLength = 64;
    public const int RejectionReasonMaxLength = 1000;

    private WorkspaceRegistrationRequest()
    {
        CompanyName = string.Empty;
    }

    public WorkspaceRegistrationRequest(
        Guid id,
        Guid applicantUserId,
        string companyName,
        string? commercialRegistrationNumber,
        DateTimeOffset requestedAt)
    {
        ValidateRequiredId(id, nameof(id));
        ValidateRequiredId(applicantUserId, nameof(applicantUserId));

        Id = id;
        ApplicantUserId = applicantUserId;
        CompanyName = TrimRequired(companyName, CompanyNameMaxLength, nameof(companyName));
        CommercialRegistrationNumber = string.IsNullOrWhiteSpace(commercialRegistrationNumber)
            ? null
            : TrimRequired(commercialRegistrationNumber, CommercialRegistrationNumberMaxLength, nameof(commercialRegistrationNumber));
        Status = WorkspaceRegistrationRequestStatus.Pending;
        RequestedAt = requestedAt;
    }

    public Guid Id { get; private set; }
    public Guid ApplicantUserId { get; private set; }
    public string CompanyName { get; private set; }
    public string? CommercialRegistrationNumber { get; private set; }
    public WorkspaceRegistrationRequestStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public Guid? ReviewedByUserId { get; private set; }
    public string? RejectionReason { get; private set; }

    /// <summary>The workspace created by the approval; null until then.</summary>
    public Guid? WorkspaceId { get; private set; }

    public void Approve(Guid reviewedByUserId, Guid workspaceId, DateTimeOffset reviewedAt)
    {
        ValidateReview(reviewedByUserId, reviewedAt);
        ValidateRequiredId(workspaceId, nameof(workspaceId));

        Status = WorkspaceRegistrationRequestStatus.Approved;
        WorkspaceId = workspaceId;
        ReviewedAt = reviewedAt;
        ReviewedByUserId = reviewedByUserId;
    }

    public void Reject(Guid reviewedByUserId, string reason, DateTimeOffset reviewedAt)
    {
        ValidateReview(reviewedByUserId, reviewedAt);

        RejectionReason = TrimRequired(reason, RejectionReasonMaxLength, nameof(reason));
        Status = WorkspaceRegistrationRequestStatus.Rejected;
        ReviewedAt = reviewedAt;
        ReviewedByUserId = reviewedByUserId;
    }

    private void ValidateReview(Guid reviewedByUserId, DateTimeOffset reviewedAt)
    {
        if (Status != WorkspaceRegistrationRequestStatus.Pending)
        {
            throw new InvalidOperationException("Only pending workspace registration requests can be reviewed.");
        }

        ValidateRequiredId(reviewedByUserId, nameof(reviewedByUserId));

        if (reviewedAt < RequestedAt)
        {
            throw new ArgumentException("Reviewed date cannot be earlier than the requested date.", nameof(reviewedAt));
        }
    }

    private static string TrimRequired(string value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"Value must not exceed {maxLength} characters.", parameterName);
        }

        return trimmed;
    }

    private static void ValidateRequiredId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", parameterName);
        }
    }
}
