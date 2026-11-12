using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Security;
using SmartProperty.Application.Identity.Bootstrap;
using SmartProperty.Application.Identity.StaffRoles;
using SmartProperty.Application.Marketplace.AuthorityRequests;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.OwnerVerification;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Domain.Workspaces;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace;

/// <summary>
/// The production advertising workflow over HTTP, with separate accounts for the owner, the legal reviewer, the
/// administrator and a customer, and nothing simulated except the malware scanner itself: evidence comes from the
/// uploaded documents, reviewer independence from the recorded relationships, staff roles from the role catalogue and
/// the staff API, and permissions from the database. The scanner is a scripted stand-in for ClamAV that answers by
/// content (the real ClamAV client is verified separately against a clamd container).
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class AuthorityRequestWorkflowTests : IDisposable
{
    private const string OwnerRoute = "/api/marketplace/me/authority-requests";
    private const string StaffRoute = "/api/admin/marketplace/authority-requests";
    private const string PublicRoute = "/api/public/marketplace/listings";

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF\n");
    private static readonly byte[] SecondPdf = Encoding.ASCII.GetBytes("%PDF-1.7\n% second version of the deed\n%%EOF\n");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] Infected = Encoding.ASCII.GetBytes($"%PDF-1.7\n{ScriptedScanner.MalwareMarker}\n%%EOF\n");
    private static readonly byte[] Unscannable = Encoding.ASCII.GetBytes($"%PDF-1.7\n{ScriptedScanner.OutageMarker}\n%%EOF\n");

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _root;
    private readonly WebApplicationFactory<Program> _host;
    private readonly AuthScenario _scenario;

    public AuthorityRequestWorkflowTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _root = new SmartPropertyApiFactory(
            fixture,
            usePersistencePermissionChecker: true,
            configuration:
            [
                new("MalwareScanning:MaxAttempts", "2"),
                new("MalwareScanning:RetryBaseDelaySeconds", "1"),
                new("MalwareScanning:RetryMaxDelayMinutes", "1"),
            ]);

        // Only the scanner is replaced. Evidence and independence are the production providers.
        _host = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMalwareScanner>();
            services.AddSingleton<IMalwareScanner>(Scanner);
        }));

        _scenario = new AuthScenario(fixture);
    }

    private ScriptedScanner Scanner { get; } = new();

    public void Dispose()
    {
        _host.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void TheProductionHostUsesTheRealEvidenceAndIndependenceSources()
    {
        using var scope = _root.Services.CreateScope();

        Assert.IsType<AuthorityDocumentEvidenceProvider>(scope.ServiceProvider.GetRequiredService<IEvidenceAssessmentProvider>());
        Assert.Equal("RecordedRelationshipIndependenceProvider",
            scope.ServiceProvider.GetRequiredService<IReviewerIndependenceProvider>().GetType().Name);
        Assert.Equal("ClamAvMalwareScanner", scope.ServiceProvider.GetRequiredService<IMalwareScanner>().GetType().Name);
        Assert.False(scope.ServiceProvider.GetRequiredService<IMalwareScanner>().IsConfigured);
    }

    [Fact]
    public async Task OwnerLawyerAdminAndCustomerGoThroughTheWholeFlowAndApprovalPublishes()
    {
        var staff = await StaffAsync();
        var world = await SeedAsync();
        var customer = await VerifiedUserAsync();
        using var owner = Client(world.Owner);
        using var lawyer = Client(staff.Lawyer);
        using var admin = Client(staff.Admin);
        using var shopper = Client(customer);

        // The owner builds the request: purposes, price, details, documents.
        var id = await CreateAsync(owner, world, ["Sale", "Rent"]);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{OwnerRoute}/{id}/listings/Sale", SaleContent())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{OwnerRoute}/{id}/listings/Rent", RentContent())).StatusCode);

        using var deedUpload = await UploadAsync(owner, id, "TitleDeed", "deed.pdf", Pdf);
        Assert.Equal(HttpStatusCode.Created, deedUpload.StatusCode);
        var deed = await ReadAsync(deedUpload);
        var deedId = deed.GetProperty("documentId").GetGuid();
        Assert.Equal("PendingScan", deed.GetProperty("scanStatus").GetString());
        Assert.Equal(64, deed.GetProperty("sha256").GetString()!.Length);

        // Quarantined: not downloadable, not submittable.
        Assert.Equal("authority_requests.document_not_available", await CodeAsync(await owner.GetAsync($"{OwnerRoute}/{id}/documents/{deedId}/file")));
        using var tooEarly = await owner.PostAsync($"{OwnerRoute}/{id}/submit", null);
        Assert.Equal("authority_requests.incomplete", await CodeAsync(tooEarly));
        Assert.Contains("DocumentScanPending", (await ReadAsync(tooEarly)).GetProperty("message").GetString());

        Assert.Equal(1, (await ScanAsync()).Clean);
        using var deedFile = await owner.GetAsync($"{OwnerRoute}/{id}/documents/{deedId}/file");
        Assert.Equal(Pdf, await deedFile.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", deedFile.Headers.GetValues("X-Content-Type-Options").Single());

        using var submitted = await owner.PostAsync($"{OwnerRoute}/{id}/submit", null);
        Assert.Equal("PendingLegalReview", (await ReadAsync(submitted)).GetProperty("status").GetString());

        // The customer sees nothing of it, and nothing is published yet.
        Assert.Equal(HttpStatusCode.NotFound, (await shopper.GetAsync($"{OwnerRoute}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await shopper.GetAsync($"{OwnerRoute}/{id}/documents/{deedId}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await shopper.GetAsync($"{StaffRoute}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await shopper.GetAsync($"{StaffRoute}/{id}/documents/{deedId}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await shopper.PostAsJsonAsync($"{StaffRoute}/{id}/legal-approve", Attested())).StatusCode);
        Assert.Empty(await OwnListingsAsync(owner));

        // The lawyer reads the package, the property and the deed.
        var package = await ReadAsync(await lawyer.GetAsync($"{StaffRoute}/{id}"));
        Assert.Equal(5_000_000m, package.GetProperty("saleListing").GetProperty("amount").GetDecimal());

        // Each listing shows only the fields of its own purpose.
        var sale = package.GetProperty("saleListing");
        var rent = package.GetProperty("rentListing");
        Assert.All(new[] { "rentPeriod", "pricingOptions", "rentalTerms" }, name => Assert.False(sale.TryGetProperty(name, out _), name));
        Assert.All(new[] { "amount", "currency", "rentPeriod" }, name => Assert.False(rent.TryGetProperty(name, out _), name));
        Assert.Equal(20_000m, rent.GetProperty("pricingOptions")[0].GetProperty("amount").GetDecimal());
        Assert.Equal(world.PropertyId, (await ReadAsync(await lawyer.GetAsync($"{StaffRoute}/{id}/property"))).GetProperty("id").GetGuid());
        Assert.Equal(Pdf, await (await lawyer.GetAsync($"{StaffRoute}/{id}/documents/{deedId}/file")).Content.ReadAsByteArrayAsync());
        Assert.Equal("ClamAV-test/1", (await ReadAsync(await lawyer.GetAsync($"{StaffRoute}/{id}/documents/{deedId}"))).GetProperty("document").GetProperty("scanEngine").GetString());

        // No final approval before the legal one, and no decision without the no-conflict confirmation.
        Assert.Equal("authority_requests.invalid_state", await CodeAsync(await admin.PostAsJsonAsync($"{StaffRoute}/{id}/issue", new { noConflictOfInterest = true })));
        Assert.Equal(HttpStatusCode.Forbidden, (await lawyer.PostAsJsonAsync($"{StaffRoute}/{id}/issue", new { noConflictOfInterest = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"{StaffRoute}/{id}/legal-approve", Attested())).StatusCode);
        Assert.Equal("authority_requests.conflict_attestation_required", await CodeAsync(await lawyer.PostAsJsonAsync($"{StaffRoute}/{id}/legal-approve", new { note = "ok" })));

        using var legalApproved = await lawyer.PostAsJsonAsync($"{StaffRoute}/{id}/legal-approve", new { note = "Deed matches the property.", noConflictOfInterest = true });
        Assert.True(legalApproved.StatusCode == HttpStatusCode.OK, await legalApproved.Content.ReadAsStringAsync());
        Assert.Equal("PendingIssuance", (await ReadAsync(legalApproved)).GetProperty("status").GetString());
        Assert.Empty(await OwnListingsAsync(owner));

        // The administrator's approval issues the grants and publishes both listings in one step.
        using var approved = await admin.PostAsJsonAsync($"{StaffRoute}/{id}/issue", new { validDays = 30, noConflictOfInterest = true });
        Assert.True(approved.StatusCode == HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        var outcome = await ReadAsync(approved);
        Assert.Equal("Approved", outcome.GetProperty("status").GetString());
        Assert.Equal(["Submitted", "LegalApproved", "AdminApproved"], Strings(outcome.GetProperty("decisions"), "kind"));
        Assert.All(outcome.GetProperty("decisions").EnumerateArray(), decision =>
            Assert.Equal(deed.GetProperty("versionId").GetGuid(), decision.GetProperty("documentVersionIds")[0].GetGuid()));
        Assert.Equal(staff.Admin, outcome.GetProperty("decisions")[2].GetProperty("actorUserId").GetGuid());

        var listings = await OwnListingsAsync(owner);
        Assert.Equal(["Rent:Approved", "Sale:Approved"], listings.Select(listing => listing.Label).Order(StringComparer.Ordinal));

        // The customer now finds the published listings.
        foreach (var listing in listings)
        {
            Assert.Equal(HttpStatusCode.OK, (await shopper.GetAsync($"{PublicRoute}/{listing.ListingId}")).StatusCode);
        }

        // The owner sees the outcome but not who decided it; an approved document can no longer be removed.
        var seen = await ReadAsync(await owner.GetAsync($"{OwnerRoute}/{id}"));
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("issuedByUserId").ValueKind);
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("decisions")[1].GetProperty("actorUserId").ValueKind);
        Assert.True(seen.GetProperty("documents")[0].GetProperty("lockedByApproval").GetBoolean());
        Assert.All((await ReadAsync(await owner.GetAsync($"/api/marketplace/me/authority-grants?propertyId={world.PropertyId}"))).EnumerateArray(),
            grant => Assert.Equal("Active", grant.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task AnInfectedFileIsRejectedAndDestroyedAndAReplacementKeepsTheHistory()
    {
        var world = await SeedAsync();
        using var owner = Client(world.Owner);
        var id = await CreateAsync(owner, world, ["Sale"], details: true);
        (await owner.PutAsJsonAsync($"{OwnerRoute}/{id}/listings/Sale", SaleContent())).Dispose();

        var bad = await ReadAsync(await UploadAsync(owner, id, "TitleDeed", "deed.pdf", Infected));
        var documentId = bad.GetProperty("documentId").GetGuid();
        Assert.Equal(1, (await ScanAsync()).Infected);

        var detail = await ReadAsync(await owner.GetAsync($"{OwnerRoute}/{id}/documents/{documentId}"));
        Assert.Equal("Rejected", detail.GetProperty("document").GetProperty("scanStatus").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("document").GetProperty("scanDetail").ValueKind); // staff-only
        Assert.Equal("authority_requests.document_not_available", await CodeAsync(await owner.GetAsync($"{OwnerRoute}/{id}/documents/{documentId}/file")));
        Assert.Contains("DocumentNotClean", Strings((await ReadAsync(await owner.GetAsync($"{OwnerRoute}/{id}"))).GetProperty("missing")));
        Assert.Equal("authority_requests.incomplete", await CodeAsync(await owner.PostAsync($"{OwnerRoute}/{id}/submit", null)));
        Assert.Empty(Directory.EnumerateFiles(_root.DocumentRoot, "*", SearchOption.AllDirectories));

        // A replacement is version 2 of the same document; version 1 stays on record.
        using var replaced = await ReplaceAsync(owner, id, documentId, "deed-clean.pdf", Pdf);
        Assert.Equal(HttpStatusCode.Created, replaced.StatusCode);
        Assert.Equal(2, (await ReadAsync(replaced)).GetProperty("versionNumber").GetInt32());
        await ScanAsync();

        var versions = (await ReadAsync(await owner.GetAsync($"{OwnerRoute}/{id}/documents/{documentId}"))).GetProperty("versions");
        Assert.Equal(["Rejected", "Clean"], Strings(versions, "scanStatus"));
        Assert.Equal(2, (await ReadAsync(await owner.GetAsync($"{OwnerRoute}/{id}/documents?includeHistory=true"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"{OwnerRoute}/{id}/submit", null)).StatusCode);
    }

    [Fact]
    public async Task SeveralDocumentsGoInOneCallAllOrNothingAndEachIsScanned()
    {
        var world = await SeedAsync();
        using var owner = Client(world.Owner);
        var id = await CreateAsync(owner, world, ["Sale"], details: true);
        (await owner.PutAsJsonAsync($"{OwnerRoute}/{id}/listings/Sale", SaleContent())).Dispose();

        // One bad file (not a document) refuses the whole batch and stores nothing.
        using var refused = await UploadBatchAsync(owner, id,
            ("TitleDeed", "deed-1.png", Png), ("TitleDeed", "deed-2.pdf", Pdf), ("Other", "notes.txt", Encoding.ASCII.GetBytes("plain text")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Empty((await ReadAsync(await owner.GetAsync($"{OwnerRoute}/{id}/documents"))).EnumerateArray());
        Assert.Empty(Directory.EnumerateFiles(_root.DocumentRoot, "*", SearchOption.AllDirectories));

        // The types must match the files one for one (or be a single type for all).
        using var mismatched = await UploadBatchAsync(owner, id, types: ["TitleDeed", "Other"], ("TitleDeed", "a.pdf", Pdf), ("TitleDeed", "b.pdf", Pdf), ("TitleDeed", "c.pdf", Pdf));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatched.StatusCode);

        // Images and PDFs together, each with its own type.
        using var created = await UploadBatchAsync(owner, id,
            ("TitleDeed", "deed-page-1.png", Png), ("TitleDeed", "deed-page-2.png", Png), ("UtilityBill", "bill.pdf", Pdf), ("Other", "plan.pdf", SecondPdf));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var documents = await ReadAsync(created);
        Assert.Equal(["TitleDeed", "TitleDeed", "UtilityBill", "Other"], Strings(documents, "type"));
        Assert.All(documents.EnumerateArray(), document => Assert.Equal("PendingScan", document.GetProperty("scanStatus").GetString()));
        Assert.Equal(4, documents.EnumerateArray().Select(document => document.GetProperty("documentId").GetGuid()).Distinct().Count());

        Assert.Equal(4, (await ScanAsync()).Clean);
        Assert.Empty(Strings((await ReadAsync(await owner.GetAsync($"{OwnerRoute}/{id}"))).GetProperty("missing")));

        // The cap (10) counts what the request already carries: 4 + 7 is one too many.
        var tooMany = Enumerable.Range(0, 7).Select(index => ("Other", $"extra-{index}.pdf", Pdf)).ToArray();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadBatchAsync(owner, id, tooMany)).StatusCode);
        Assert.Equal(4, (await ReadAsync(await owner.GetAsync($"{OwnerRoute}/{id}/documents"))).GetArrayLength());

        // Types written as API clients send array fields (a JSON array per value) are understood too.
        using var asJson = await UploadBatchAsync(owner, id, types: ["[\"UtilityBill\"]", "[\"Other\"]"], ("", "bill-2.pdf", Pdf), ("", "misc.pdf", SecondPdf));
        Assert.Equal(HttpStatusCode.Created, asJson.StatusCode);
        Assert.Equal(["UtilityBill", "Other"], Strings(await ReadAsync(asJson), "type"));
        await ScanAsync();

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"{OwnerRoute}/{id}/submit", null)).StatusCode);
        Assert.Equal("authority_requests.not_editable", await CodeAsync(await UploadBatchAsync(owner, id, ("Other", "late.pdf", Pdf))));
    }

    [Fact]
    public async Task WithoutAScannerNothingIsCleanAndAnOutageEndsInScanFailedWhichCanBeRescanned()
    {
        var world = await SeedAsync();
        using var owner = Client(world.Owner);
        var id = await CreateAsync(owner, world, ["Sale"], details: true);
        (await owner.PutAsJsonAsync($"{OwnerRoute}/{id}/listings/Sale", SaleContent())).Dispose();
        var documentId = (await ReadAsync(await UploadAsync(owner, id, "TitleDeed", "deed.pdf", Unscannable))).GetProperty("documentId").GetGuid();

        // No scanner: the queue is not touched and the document stays quarantined.
        Scanner.Configured = false;
        Assert.Equal(0, (await ScanAsync()).Processed);
        Assert.Equal("PendingScan", await ScanStatusAsync(owner, id, documentId));
        Scanner.Configured = true;

        // The scanner answers with errors: retried once, then ScanFailed (MaxAttempts = 2).
        Assert.Equal(1, (await ScanAsync()).Retried);
        Assert.Equal(0, (await ScanAsync()).Processed); // the retry is not due yet
        await Task.Delay(TimeSpan.FromSeconds(2.2));
        Assert.Equal(1, (await ScanAsync()).Failed);
        Assert.Equal("ScanFailed", await ScanStatusAsync(owner, id, documentId));
        Assert.Equal("authority_requests.incomplete", await CodeAsync(await owner.PostAsync($"{OwnerRoute}/{id}/submit", null)));

        // A rescan starts a fresh set of attempts; once the scanner recovers the same bytes are judged.
        Scanner.TreatOutageMarkerAsClean = true;
        using var rescan = await owner.PostAsync($"{OwnerRoute}/{id}/documents/{documentId}/rescan", null);
        Assert.Equal("PendingScan", (await ReadAsync(rescan)).GetProperty("scanStatus").GetString());
        Assert.Equal("authority_requests.document_not_rescannable", await CodeAsync(await owner.PostAsync($"{OwnerRoute}/{id}/documents/{documentId}/rescan", null)));
        Assert.Equal(1, (await ScanAsync()).Clean);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"{OwnerRoute}/{id}/submit", null)).StatusCode);
    }

    [Fact]
    public async Task AFileWhoseStoredBytesChangedIsNeverMarkedClean()
    {
        var world = await SeedAsync();
        using var owner = Client(world.Owner);
        var id = await CreateAsync(owner, world, ["Sale"], details: true);
        var documentId = (await ReadAsync(await UploadAsync(owner, id, "TitleDeed", "deed.pdf", Pdf))).GetProperty("documentId").GetGuid();

        foreach (var file in Directory.EnumerateFiles(_root.DocumentRoot, "*", SearchOption.AllDirectories))
        {
            await File.WriteAllBytesAsync(file, SecondPdf);
        }

        Assert.Equal(1, (await ScanAsync()).Retried);
        Assert.Equal("PendingScan", await ScanStatusAsync(owner, id, documentId));
        Assert.Equal(0, Scanner.Scans);
    }

    [Fact]
    public async Task TheLawyerCanAskForCompletionAndTheAdminTooAndBothReviewsStartOver()
    {
        var staff = await StaffAsync();
        var world = await SeedAsync();
        using var owner = Client(world.Owner);
        using var lawyer = Client(staff.Lawyer);
        using var admin = Client(staff.Admin);
        var (id, deedId) = await SubmittedAsync(owner, world, ["Sale"]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await lawyer.PostAsJsonAsync($"{StaffRoute}/{id}/request-completion", new { reason = " ", noConflictOfInterest = true })).StatusCode);
        using var asked = await lawyer.PostAsJsonAsync($"{StaffRoute}/{id}/request-completion", new { reason = "Upload every page of the deed.", noConflictOfInterest = true });
        var draft = await ReadAsync(await owner.GetAsync($"{OwnerRoute}/{id}"));
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Equal("Upload every page of the deed.", draft.GetProperty("completionRequestReason").GetString());
        Assert.Equal("PendingLegalReview", draft.GetProperty("completionRequestedAtStage").GetString());

        // Not yet approved, so the deed may be replaced (and even removed); here it is replaced.
        Assert.Equal(HttpStatusCode.Created, (await ReplaceAsync(owner, id, deedId, "deed-all-pages.pdf", SecondPdf)).StatusCode);
        await ScanAsync();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"{OwnerRoute}/{id}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lawyer.PostAsJsonAsync($"{StaffRoute}/{id}/legal-approve", Attested())).StatusCode);

        // The administrator asks for completion too: the deed was approved, so it can be replaced but not removed.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{StaffRoute}/{id}/request-completion", new { reason = "Lower the price.", noConflictOfInterest = true })).StatusCode);
        Assert.Equal("authority_requests.document_locked_by_approval", await CodeAsync(await owner.DeleteAsync($"{OwnerRoute}/{id}/documents/{deedId}")));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{OwnerRoute}/{id}/listings/Sale", SaleContent(4_000_000m))).StatusCode);

        // Resubmitted: back to the lawyer, never straight to the administrator.
        Assert.Equal("PendingLegalReview", (await ReadAsync(await owner.PostAsync($"{OwnerRoute}/{id}/submit", null))).GetProperty("status").GetString());
        Assert.Equal("authority_requests.invalid_state", await CodeAsync(await admin.PostAsJsonAsync($"{StaffRoute}/{id}/issue", new { noConflictOfInterest = true })));

        var history = await ReadAsync(await admin.GetAsync($"{StaffRoute}/{id}"));
        Assert.Equal(
            ["Submitted", "LegalCompletionRequested", "Submitted", "LegalApproved", "AdminCompletionRequested", "Submitted"],
            Strings(history.GetProperty("decisions"), "kind"));
        Assert.Equal(2, (await ReadAsync(await admin.GetAsync($"{StaffRoute}/{id}/documents?includeHistory=true"))).GetArrayLength());
    }

    [Fact]
    public async Task ARejectionIsFinalAndTheOwnerCanStartANewRequest()
    {
        var staff = await StaffAsync();
        var world = await SeedAsync();
        using var owner = Client(world.Owner);
        using var lawyer = Client(staff.Lawyer);
        var (id, _) = await SubmittedAsync(owner, world, ["Sale"]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await lawyer.PostAsJsonAsync($"{StaffRoute}/{id}/reject", new { noConflictOfInterest = true })).StatusCode);
        using var rejected = await lawyer.PostAsJsonAsync($"{StaffRoute}/{id}/reject", new { reason = "The deed names another owner.", noConflictOfInterest = true });
        var body = await ReadAsync(rejected);

        Assert.Equal("Rejected", body.GetProperty("status").GetString());
        Assert.Equal("The deed names another owner.", body.GetProperty("rejectionReason").GetString());
        Assert.Equal("authority_requests.invalid_state", await CodeAsync(await owner.PostAsync($"{OwnerRoute}/{id}/submit", null)));
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(OwnerRoute, new { propertyId = world.PropertyId, purposes = new[] { "Sale" } })).StatusCode);
    }

    [Fact]
    public async Task AChangeToThePropertyAfterTheLegalReviewBlocksTheFinalApproval()
    {
        var staff = await StaffAsync();
        var world = await SeedAsync();
        using var owner = Client(world.Owner);
        using var lawyer = Client(staff.Lawyer);
        using var admin = Client(staff.Admin);
        var (id, _) = await SubmittedAsync(owner, world, ["Sale"]);
        Assert.Equal(HttpStatusCode.OK, (await lawyer.PostAsJsonAsync($"{StaffRoute}/{id}/legal-approve", Attested())).StatusCode);

        await using (var context = _fixture.CreateContext())
        {
            await context.Properties.Where(property => property.Id == world.PropertyId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(property => property.UpdatedAt, DateTimeOffset.UtcNow));
        }

        Assert.Equal("authority_requests.property_changed_since_review",
            await CodeAsync(await admin.PostAsJsonAsync($"{StaffRoute}/{id}/issue", new { noConflictOfInterest = true })));
        Assert.Empty(await OwnListingsAsync(owner));
    }

    [Fact]
    public async Task AReviewerWhoSharesAWorkspaceWithTheOwnerIsNotIndependent()
    {
        var staff = await StaffAsync();
        var world = await SeedAsync();
        using var owner = Client(world.Owner);
        using var lawyer = Client(staff.Lawyer);
        var (id, _) = await SubmittedAsync(owner, world, ["Sale"]);

        await using (var context = _fixture.CreateContext())
        {
            var workspace = await context.WorkspaceMemberships.Where(membership => membership.UserId == world.Owner)
                .Select(membership => membership.WorkspaceId).FirstAsync();
            context.WorkspaceMemberships.Add(new WorkspaceMembership(Guid.NewGuid(), staff.Lawyer, workspace, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        using var decided = await lawyer.PostAsJsonAsync($"{StaffRoute}/{id}/legal-approve", Attested());

        Assert.Equal(HttpStatusCode.Forbidden, decided.StatusCode);
        Assert.Equal("authority_requests.reviewer_not_independent", await CodeAsync(decided));
    }

    [Fact]
    public async Task TheLegalReviewerCannotAlsoTakeTheFinalDecisionEvenWithBothRoles()
    {
        var staff = await StaffAsync();
        var world = await SeedAsync();
        using var owner = Client(world.Owner);
        await AssignRoleAsync(staff.Lawyer, BootstrapPlatformAdminCommandHandler.PlatformAdminRoleName);
        using var both = Client(staff.Lawyer);
        var (id, _) = await SubmittedAsync(owner, world, ["Sale"]);

        Assert.Equal(HttpStatusCode.OK, (await both.PostAsJsonAsync($"{StaffRoute}/{id}/legal-approve", Attested())).StatusCode);
        Assert.Equal("authority_requests.issuer_is_legal_reviewer", await CodeAsync(await both.PostAsJsonAsync($"{StaffRoute}/{id}/issue", new { noConflictOfInterest = true })));
        Assert.Equal("authority_requests.issuer_is_legal_reviewer", await CodeAsync(await both.PostAsJsonAsync($"{StaffRoute}/{id}/reject", new { reason = "x", noConflictOfInterest = true })));
    }

    [Fact]
    public async Task DocumentsAreGuardedForEveryoneButTheOwnerAndStaffAndDraftsStayPrivate()
    {
        var staff = await StaffAsync();
        var world = await SeedAsync();
        var stranger = await VerifiedUserAsync();
        using var owner = Client(world.Owner);
        using var other = Client(stranger);
        using var lawyer = Client(staff.Lawyer);
        var id = await CreateAsync(owner, world, ["Sale"], details: true);
        var documentId = (await ReadAsync(await UploadAsync(owner, id, "TitleDeed", "deed.pdf", Pdf))).GetProperty("documentId").GetGuid();
        await ScanAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{OwnerRoute}/{id}/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{OwnerRoute}/{id}/documents/{documentId}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(other, id, "TitleDeed", "x.pdf", Pdf)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ReplaceAsync(other, id, documentId, "x.pdf", Pdf)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{OwnerRoute}/{id}/documents/{documentId}")).StatusCode);

        // Staff see nothing of a draft until it is submitted.
        Assert.Equal(HttpStatusCode.NotFound, (await lawyer.GetAsync($"{StaffRoute}/{id}/documents/{documentId}/file")).StatusCode);

        // Anonymous callers get nothing.
        using var anonymous = _host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{OwnerRoute}/{id}/documents/{documentId}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{StaffRoute}/{id}/documents")).StatusCode);

        // Wrong type by content, unknown type names, and a document type given as a number are refused.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(owner, id, "TitleDeed", "deed.pdf", Encoding.ASCII.GetBytes("<html>no</html>"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(owner, id, "Passport", "deed.pdf", Pdf)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(owner, id, "1", "deed.pdf", Pdf)).StatusCode);

        // A draft document that was never approved can be removed; its record stays in the history.
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(owner, id, "BrokerageLicense", "license.png", Png)).StatusCode);
        var license = (await ReadAsync(await owner.GetAsync($"{OwnerRoute}/{id}/documents"))).EnumerateArray()
            .Single(document => document.GetProperty("type").GetString() == "BrokerageLicense").GetProperty("documentId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"{OwnerRoute}/{id}/documents/{license}")).StatusCode);
        Assert.Equal(1, (await ReadAsync(await owner.GetAsync($"{OwnerRoute}/{id}/documents"))).GetArrayLength());
        Assert.Equal(2, (await ReadAsync(await owner.GetAsync($"{OwnerRoute}/{id}/documents?includeHistory=true"))).GetArrayLength());
    }

    [Fact]
    public async Task OnlyAPlatformAdministratorAssignsLegalReviewersAndNotThemselves()
    {
        var staff = await StaffAsync();
        var candidate = await VerifiedUserAsync();
        var unverified = await ActiveUserAsync();
        using var admin = Client(staff.Admin);
        using var lawyer = Client(staff.Lawyer);
        var adminEmail = await EmailAsync(staff.Admin);

        Assert.Equal(HttpStatusCode.Forbidden, (await lawyer.PostAsJsonAsync("/api/admin/staff/legal-reviewers", new { email = await EmailAsync(candidate) })).StatusCode);
        Assert.Equal("staff.cannot_assign_self", await CodeAsync(await admin.PostAsJsonAsync("/api/admin/staff/legal-reviewers", new { email = adminEmail })));
        Assert.Equal("staff.user_not_eligible", await CodeAsync(await admin.PostAsJsonAsync("/api/admin/staff/legal-reviewers", new { email = await EmailAsync(unverified) })));

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/admin/staff/legal-reviewers", new { email = await EmailAsync(candidate) })).StatusCode);
        var listed = await ReadAsync(await admin.GetAsync("/api/admin/staff/legal-reviewers"));
        Assert.Contains(listed.EnumerateArray(), member => member.GetProperty("userId").GetGuid() == candidate);

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/admin/staff/legal-reviewers/{candidate}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/staff/legal-reviewers/{candidate}")).StatusCode);
    }

    // ---- helpers

    private sealed record World(Guid PropertyId, Guid Owner);

    private sealed record Staff(Guid Admin, Guid Lawyer);

    private sealed record OwnListing(Guid ListingId, string Label);

    private static object Attested() => new { noConflictOfInterest = true };

    private static object SaleContent(decimal amount = 5_000_000m) => new
    {
        title = "Villa for sale", description = "A villa", amount, currency = "EGP", evidenceIds = Array.Empty<Guid>(),
    };

    private static object RentContent() => new
    {
        title = "Villa for rent",
        description = "A villa",
        evidenceIds = Array.Empty<Guid>(),
        pricingOptions = new object[] { new { period = "Monthly", amount = 20_000m, currency = "EGP" } },
        rentalTerms = new
        {
            checkInTime = "14:00:00",
            checkOutTime = "11:00:00",
            windows = new object[] { new { startDate = "2027-06-01", endDate = "2027-09-01" } },
        },
    };

    private async Task<DocumentScanPassResult> ScanAsync()
    {
        using var scope = _host.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AuthorityDocumentScanProcessor>().ProcessDueAsync();
    }

    private static async Task<string?> ScanStatusAsync(HttpClient owner, Guid id, Guid documentId) =>
        (await ReadAsync(await owner.GetAsync($"{OwnerRoute}/{id}/documents/{documentId}"))).GetProperty("document").GetProperty("scanStatus").GetString();

    private static async Task<Guid> CreateAsync(HttpClient owner, World world, string[] purposes, bool details = false)
    {
        using var created = await owner.PostAsJsonAsync(OwnerRoute, details
            ? new { propertyId = world.PropertyId, purposes, capacity = "Owner", ownershipDocumentNumber = "DEED-12345" }
            : (object)new { propertyId = world.PropertyId, purposes });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadAsync(created)).GetProperty("requestId").GetGuid();

        if (!details)
        {
            using var updated = await owner.PutAsJsonAsync($"{OwnerRoute}/{id}", new { capacity = "Owner", ownershipDocumentNumber = "DEED-12345" });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        }

        return id;
    }

    /// <summary>A complete owner request with a clean deed, submitted.</summary>
    private async Task<(Guid RequestId, Guid DeedId)> SubmittedAsync(HttpClient owner, World world, string[] purposes)
    {
        var id = await CreateAsync(owner, world, purposes, details: true);

        foreach (var purpose in purposes)
        {
            using var saved = await owner.PutAsJsonAsync($"{OwnerRoute}/{id}/listings/{purpose}", purpose == "Sale" ? SaleContent() : RentContent());
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        var deedId = (await ReadAsync(await UploadAsync(owner, id, "TitleDeed", "deed.pdf", Pdf))).GetProperty("documentId").GetGuid();
        await ScanAsync();
        using var submitted = await owner.PostAsync($"{OwnerRoute}/{id}/submit", null);
        Assert.True(submitted.StatusCode == HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());

        return (id, deedId);
    }

    private static async Task<OwnListing[]> OwnListingsAsync(HttpClient owner)
    {
        var body = await ReadAsync(await owner.GetAsync("/api/marketplace/me/listing-submissions?pageSize=100"));

        return body.GetProperty("items").EnumerateArray()
            .Select(item => new OwnListing(
                item.GetProperty("listingId").GetGuid(),
                $"{item.GetProperty("purpose").GetString()}:{item.GetProperty("status").GetString()}"))
            .ToArray();
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid requestId, string type, string name, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        form.Add(new StringContent(type), "documentType");

        return client.PostAsync($"{OwnerRoute}/{requestId}/documents", form);
    }

    private static Task<HttpResponseMessage> UploadBatchAsync(
        HttpClient client, Guid requestId, params (string Type, string Name, byte[] Bytes)[] files) =>
        UploadBatchAsync(client, requestId, files.Select(file => file.Type).ToArray(), files);

    private static Task<HttpResponseMessage> UploadBatchAsync(
        HttpClient client, Guid requestId, string[] types, params (string Type, string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();

        foreach (var (_, name, bytes) in files)
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "files", name);
        }

        foreach (var type in types)
        {
            form.Add(new StringContent(type), "documentTypes");
        }

        return client.PostAsync($"{OwnerRoute}/{requestId}/documents/batch", form);
    }

    private static Task<HttpResponseMessage> ReplaceAsync(HttpClient client, Guid requestId, Guid documentId, string name, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", name);

        return client.PutAsync($"{OwnerRoute}/{requestId}/documents/{documentId}", form);
    }

    private static string[] Strings(JsonElement array, string? property = null) =>
        array.EnumerateArray().Select(item => (property is null ? item : item.GetProperty(property)).GetString()!).ToArray();

    private HttpClient Client(Guid userId) => AuthScenario.Authenticate(_host.CreateClient(), TestJwt.CreateAccessToken(userId));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetString();

    private async Task<Guid> ActiveUserAsync()
    {
        using var client = _root.CreateClient();

        return (await _scenario.RegisterActiveUserAsync(client)).UserId;
    }

    private async Task<Guid> VerifiedUserAsync()
    {
        var user = await ActiveUserAsync();
        await _scenario.UpdateUserAsync(user, (candidate, now) => candidate.VerifyEmail(now));

        return user;
    }

    private async Task<string> EmailAsync(Guid userId)
    {
        await using var context = _fixture.CreateContext();

        return await context.Users.Where(user => user.Id == userId).Select(user => user.Email).SingleAsync();
    }

    /// <summary>
    /// A platform administrator (the Platform Admin role, as the bootstrap provisions it, completed by the staff role
    /// catalogue) and a legal reviewer the administrator appoints through the staff API.
    /// </summary>
    private async Task<Staff> StaffAsync()
    {
        var admin = await VerifiedUserAsync();
        await AssignRoleAsync(admin, BootstrapPlatformAdminCommandHandler.PlatformAdminRoleName);

        using (var scope = _host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MarketplaceStaffRoleCatalog>().EnsureAsync();
        }

        var lawyer = await VerifiedUserAsync();
        using var adminClient = Client(admin);
        using var appointed = await adminClient.PostAsJsonAsync("/api/admin/staff/legal-reviewers", new { email = await EmailAsync(lawyer) });
        Assert.True(appointed.StatusCode == HttpStatusCode.OK, await appointed.Content.ReadAsStringAsync());

        return new Staff(admin, lawyer);
    }

    private async Task AssignRoleAsync(Guid userId, string roleName)
    {
        await using var context = _fixture.CreateContext();

        var role = await context.Roles.SingleOrDefaultAsync(candidate => candidate.Name == roleName && candidate.Scope == RoleScope.Platform);

        if (role is null)
        {
            role = new Role(Guid.NewGuid(), roleName, RoleScope.Platform, null, DateTimeOffset.UtcNow);
            context.Roles.Add(role);
        }

        context.UserPlatformRoles.Add(new UserPlatformRole(userId, role.Id));
        await context.SaveChangesAsync();
    }

    /// <summary>An owner with an approved owner profile, and a property registered by them.</summary>
    private async Task<World> SeedAsync()
    {
        var owner = await ActiveUserAsync();
        var profileReviewer = await ActiveUserAsync();
        var at = DateTimeOffset.UtcNow.AddDays(-30);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var aiId = Random.Shared.Next(10_000_000, 2_000_000_000);
        var country = new SmartProperty.Domain.Locations.Country(Guid.NewGuid(), aiId, $"Country {aiId}", "بلد", "EG");
        var city = new SmartProperty.Domain.Locations.City(Guid.NewGuid(), aiId, country, "Cairo", "القاهرة");
        var area = new SmartProperty.Domain.Locations.Area(Guid.NewGuid(), aiId, city, "Maadi", "المعادي");
        var property = new Property(
            Guid.NewGuid(),
            PropertyType.Villa,
            new PropertyAddress("EG", city: "Cairo", region: "Cairo Governorate", district: "Maadi"),
            at,
            bedrooms: 3,
            bathrooms: 2,
            physical: new PropertyPhysicalInfo(areaSqm: 150m, timeZoneId: "Africa/Cairo"),
            area: area,
            ownerUserId: owner);

        var profile = new OwnerProfile(Guid.NewGuid(), owner, at);
        profile.SaveIdentity("SA", $"1{Random.Shared.NextInt64(100_000_000, 999_999_999)}", "Saudi", new DateOnly(1990, 1, 1), today.AddYears(5), true, today, at);
        profile.SaveAddress("12", "Street", "District", "Cairo", "11728", null, true, at);
        profile.SetAddressProof("owner-profiles/proof.png", "image/png", "proof.png", at);
        profile.Submit(at);
        profile.Approve(profileReviewer, at.AddMinutes(1));

        await using var context = _fixture.CreateContext();
        context.Areas.Add(area);
        context.Properties.Add(property);
        context.OwnerProfiles.Add(profile);
        await context.SaveChangesAsync();

        return new World(property.Id, owner);
    }

    /// <summary>
    /// Stands in for clamd: infected when the bytes carry <see cref="MalwareMarker"/>, an error (scanner outage) when they
    /// carry <see cref="OutageMarker"/>, clean otherwise. A harmless marker is used rather than the EICAR string so that
    /// no antivirus on a developer machine quarantines this test assembly.
    /// </summary>
    private sealed class ScriptedScanner : IMalwareScanner
    {
        public const string MalwareMarker = "SMARTPROPERTY-TEST-MALWARE-MARKER";
        public const string OutageMarker = "SMARTPROPERTY-TEST-SCANNER-OUTAGE";

        public bool Configured { get; set; } = true;

        public bool TreatOutageMarkerAsClean { get; set; }

        public int Scans { get; private set; }

        public bool IsConfigured => Configured;

        public async Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
        {
            Scans++;
            using var reader = new StreamReader(content, Encoding.ASCII);
            var text = await reader.ReadToEndAsync(cancellationToken);

            if (text.Contains(MalwareMarker, StringComparison.Ordinal))
            {
                return new MalwareScanResult(MalwareScanVerdict.Infected, "ClamAV-test/1", "Test.Malware.Marker");
            }

            return text.Contains(OutageMarker, StringComparison.Ordinal) && !TreatOutageMarkerAsClean
                ? new MalwareScanResult(MalwareScanVerdict.Error, "ClamAV-test/1", "Connection refused")
                : new MalwareScanResult(MalwareScanVerdict.Clean, "ClamAV-test/1", null);
        }
    }
}
