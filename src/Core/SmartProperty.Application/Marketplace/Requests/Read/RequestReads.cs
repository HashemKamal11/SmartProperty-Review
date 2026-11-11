using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.Requests.Read;

/// <summary>The caller's own requests, as a customer.</summary>
public sealed record GetMyRequestsQuery(RequestKind? Kind, RequestStatusFilter? Status, int? Page, int? PageSize)
    : IQuery<PagedList<MarketplaceRequestView>>;

public sealed record GetMyRequestQuery(RequestKind Kind, Guid RequestId) : IQuery<MarketplaceRequestView>;

/// <summary>Requests made against listings the caller advertises.</summary>
public sealed record GetReceivedRequestsQuery(
    RequestKind? Kind, Guid? ListingId, RequestStatusFilter? Status, int? Page, int? PageSize)
    : IQuery<PagedList<MarketplaceRequestView>>;

public sealed record GetReceivedRequestQuery(RequestKind Kind, Guid RequestId) : IQuery<MarketplaceRequestView>;

/// <summary>The caller's commitments, as the customer or as the owner.</summary>
public sealed record GetMyCommitmentsQuery(CommitmentRole Role, Domain.Marketplace.CommitmentStatus? Status, int? Page, int? PageSize)
    : IQuery<PagedList<CommitmentView>>;

public sealed record GetMyCommitmentQuery(Guid CommitmentId) : IQuery<CommitmentView>;

/// <summary>
/// The read handlers. Each resolves the caller from <see cref="ICurrentUser"/> alone, requires an Active account, and
/// hands the repository nothing but that identity: whose rows are visible is decided in the database, never by a
/// request value. A row that is another person's and a row that does not exist are the same not-found.
/// </summary>
public sealed class RequestReadHandlers(
    ICurrentUser currentUser,
    IUserRepository users,
    IMarketplaceRequestReadRepository reads,
    IDateTimeProvider clock)
    : IQueryHandler<GetMyRequestsQuery, PagedList<MarketplaceRequestView>>,
      IQueryHandler<GetMyRequestQuery, MarketplaceRequestView>,
      IQueryHandler<GetReceivedRequestsQuery, PagedList<MarketplaceRequestView>>,
      IQueryHandler<GetReceivedRequestQuery, MarketplaceRequestView>,
      IQueryHandler<GetMyCommitmentsQuery, PagedList<CommitmentView>>,
      IQueryHandler<GetMyCommitmentQuery, CommitmentView>
{
    public async Task<Result<PagedList<MarketplaceRequestView>>> Handle(
        GetMyRequestsQuery query, CancellationToken cancellationToken = default)
    {
        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } error)
        {
            return Result<PagedList<MarketplaceRequestView>>.Failure(error);
        }

        if (Invalid(query.Kind, query.Status) is { } invalid)
        {
            return Result<PagedList<MarketplaceRequestView>>.Failure(invalid);
        }

        var paging = Paging(query.Page, query.PageSize);
        var page = await reads.ListCustomerRequestsAsync(acting.UserId, query.Kind, query.Status, paging.Offset, paging.PageSize, cancellationToken);

        return Result<PagedList<MarketplaceRequestView>>.Success(
            PagedList<MarketplaceRequestView>.Create(page.Items, paging.PageNumber, paging.PageSize, page.TotalCount));
    }

    public async Task<Result<MarketplaceRequestView>> Handle(GetMyRequestQuery query, CancellationToken cancellationToken = default)
    {
        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } error)
        {
            return Result<MarketplaceRequestView>.Failure(error);
        }

        var view = query.RequestId == Guid.Empty || !Enum.IsDefined(query.Kind)
            ? null
            : await reads.GetCustomerRequestAsync(acting.UserId, query.Kind, query.RequestId, cancellationToken);

        return view is null
            ? Result<MarketplaceRequestView>.Failure(RequestErrors.RequestNotFound)
            : Result<MarketplaceRequestView>.Success(view);
    }

    public async Task<Result<PagedList<MarketplaceRequestView>>> Handle(
        GetReceivedRequestsQuery query, CancellationToken cancellationToken = default)
    {
        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } error)
        {
            return Result<PagedList<MarketplaceRequestView>>.Failure(error);
        }

        if (Invalid(query.Kind, query.Status) is { } invalid)
        {
            return Result<PagedList<MarketplaceRequestView>>.Failure(invalid);
        }

        var paging = Paging(query.Page, query.PageSize);
        var page = await reads.ListReceivedRequestsAsync(
            acting.UserId, query.Kind, query.ListingId, query.Status, paging.Offset, paging.PageSize, cancellationToken);

        return Result<PagedList<MarketplaceRequestView>>.Success(
            PagedList<MarketplaceRequestView>.Create(page.Items, paging.PageNumber, paging.PageSize, page.TotalCount));
    }

    public async Task<Result<MarketplaceRequestView>> Handle(GetReceivedRequestQuery query, CancellationToken cancellationToken = default)
    {
        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } error)
        {
            return Result<MarketplaceRequestView>.Failure(error);
        }

        var view = query.RequestId == Guid.Empty || !Enum.IsDefined(query.Kind)
            ? null
            : await reads.GetReceivedRequestAsync(acting.UserId, query.Kind, query.RequestId, cancellationToken);

        return view is null
            ? Result<MarketplaceRequestView>.Failure(RequestErrors.RequestNotFound)
            : Result<MarketplaceRequestView>.Success(view);
    }

    public async Task<Result<PagedList<CommitmentView>>> Handle(GetMyCommitmentsQuery query, CancellationToken cancellationToken = default)
    {
        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } error)
        {
            return Result<PagedList<CommitmentView>>.Failure(error);
        }

        if (!Enum.IsDefined(query.Role) || (query.Status is { } status && !Enum.IsDefined(status)))
        {
            return Result<PagedList<CommitmentView>>.Failure(RequestErrors.Invalid("The role or status is not supported."));
        }

        var paging = Paging(query.Page, query.PageSize);
        var page = await reads.ListCommitmentsAsync(
            clock.UtcNow, acting.UserId, query.Role, query.Status, paging.Offset, paging.PageSize, cancellationToken);

        return Result<PagedList<CommitmentView>>.Success(
            PagedList<CommitmentView>.Create(page.Items, paging.PageNumber, paging.PageSize, page.TotalCount));
    }

    public async Task<Result<CommitmentView>> Handle(GetMyCommitmentQuery query, CancellationToken cancellationToken = default)
    {
        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } error)
        {
            return Result<CommitmentView>.Failure(error);
        }

        var view = query.CommitmentId == Guid.Empty
            ? null
            : await reads.GetCommitmentAsync(clock.UtcNow, acting.UserId, query.CommitmentId, cancellationToken);

        return view is null
            ? Result<CommitmentView>.Failure(RequestErrors.CommitmentNotFound)
            : Result<CommitmentView>.Success(view);
    }

    private static Error? Invalid(RequestKind? kind, RequestStatusFilter? status)
    {
        return (kind is { } k && !Enum.IsDefined(k)) || (status is { } s && !Enum.IsDefined(s))
            ? RequestErrors.Invalid("The kind or status is not supported.")
            : null;
    }

    private static PageParameters Paging(int? page, int? pageSize)
    {
        var parameters = new PageParameters();

        if (page is { } p)
        {
            parameters.PageNumber = p;
        }

        if (pageSize is { } s)
        {
            parameters.PageSize = s;
        }

        return parameters;
    }
}
