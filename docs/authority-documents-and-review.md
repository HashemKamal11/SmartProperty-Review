# Advertising authority: documents, malware scanning, legal and administrative review

This is the production path from "the owner has a property" to "its listings are published":

```
owner: property → authority request (purposes, price/terms, capacity) → documents (scanned) → submit
legal reviewer: checks documents + property data → approve | ask for completion | reject
platform administrator (a different person): re-checks everything → approve (grants + publication, atomically)
                                                                   | ask for completion | reject
```

Nothing in this path uses demo mode, a Development tool, a simulated evidence provider or a hand-written grant. Payments
are out of scope.

## 1. Accounts and roles

| Who | How they get access | What they can do |
|---|---|---|
| Owner | Any Active account that registered the property and has an **approved owner profile** | Create/edit their own request while it is a Draft, upload/replace/remove documents, submit, withdraw, download their own Clean documents |
| Legal reviewer | Role **Legal Reviewer** (`marketplace.authority.legal_reviews.record`, `marketplace.authority.grants.read`), assigned by a platform administrator through `POST /api/admin/staff/legal-reviewers` | Read submitted requests, the property and Clean documents; approve, ask for completion, or reject at legal review |
| Platform administrator | Role **Platform Admin** (created by the bootstrap); at every API start the staff role catalogue adds `marketplace.authority.grants.issue`, `...grants.read`, `...grants.revoke`, `marketplace.settings.manage`, `platform.staff.manage` | Final approval (issues grants and publishes), ask for completion, reject; revoke grants; appoint legal reviewers |
| Customer | Any account | Sees only published listings. Has no access to requests or documents |

Separation of duties is enforced in the Domain, the database and the API:

- the requester can never decide on their own request;
- the person who gave the legal approval can never take the final decision on the same request, even holding both roles;
- the administrator does not hold the legal-review permission, and nobody can change their own staff roles.

## 2. Owner API (`/api/marketplace/me/authority-requests`)

| Method and path | Purpose |
|---|---|
| `POST /` `{propertyId, purposes:["Sale","Rent"], capacity?, ownershipDocumentNumber?, ...}` | Start a Draft |
| `PUT /{id}/listings/{Sale\|Rent}` (listing-draft body) | Price and terms, validated against the property now. `evidenceIds` is ignored |
| `PUT /{id}` `{capacity, ownershipDocumentNumber, brokerageLicenseNumber?, agencyName?, propertyOwnerName?}` | Capacity and details |
| `POST /{id}/documents` multipart: `file`, `documentType` | Upload. **201** with the document: `documentId`, `versionId`, `sha256`, `scanStatus: "PendingScan"` |
| `POST /{id}/documents/batch` multipart: repeated `files` and `documentTypes` (same order, or one type for all) | Upload several at once, all or nothing (at most 10 documents in force per request, 10 MB each). **201** with the array of documents, in the order sent |
| `GET /{id}/documents[?includeHistory=true]` | Documents in force (or every version ever stored) |
| `GET /{id}/documents/{documentId}` | One document with all its versions |
| `PUT /{id}/documents/{documentId}` multipart: `file` | Replace (same type). **201** with version N+1; earlier versions are kept |
| `DELETE /{id}/documents/{documentId}` | Remove from the draft (the record and bytes stay). **409 `document_locked_by_approval`** if any version was part of an approval |
| `POST /{id}/documents/{documentId}/rescan` | Queue a `ScanFailed` document for a fresh set of attempts |
| `GET /{id}/documents/{documentId}/file` | Download the version in force. Only `Clean`; otherwise **409 `document_not_available`** |
| `GET /{id}/documents/{documentId}/versions/{versionId}/file` | Download one version (only `Clean`) |
| `POST /{id}/submit` | Needs `missing` empty, including `DocumentScanPending` and `DocumentNotClean` |
| `POST /{id}/withdraw` | Before a final decision |
| `GET /`, `GET /{id}`, `GET /api/marketplace/me/authority-grants` | Read |

`documentType`: `TitleDeed`, `SaleContract` (one of the two is required), `Authorization` and `BrokerageLicense`
(both required for `IndividualRepresentative`), `UtilityBill`, `Other`. `GET /api/marketplace/authority-document-types`
describes them in English and Arabic.

The listing's evidence is **the request's own documents in force**. The client never needs or sends an evidence id;
the old `POST /api/development/evidence` endpoint is not part of this path.

A request another user owns is answered 404, so its existence is not revealed.

## 3. Staff API

Legal reviewer and administrator (`/api/admin/marketplace/authority-requests`):

| Method and path | Who | Purpose |
|---|---|---|
| `GET /?status=PendingLegalReview\|PendingIssuance\|Approved\|Rejected\|Withdrawn` | any authority permission | Queue (waiting work oldest first) |
| `GET /{id}` | " | Request with listing content, documents, decision history (with actor ids) |
| `GET /{id}/property` | " | The property as registered now |
| `GET /{id}/documents[?includeHistory=true]`, `GET /{id}/documents/{documentId}` | " | Documents; staff also see `scanEngine` and `scanDetail` |
| `GET /{id}/documents/{documentId}/file`, `.../versions/{versionId}/file` | " | Download (Clean only) |
| `POST /{id}/legal-approve` `{note?, noConflictOfInterest:true}` | legal reviewer | → `PendingIssuance`. Nothing is published |
| `POST /{id}/issue` `{validDays?, noConflictOfInterest:true}` | administrator | Final approval (see §5) |
| `POST /{id}/request-completion` `{reason, noConflictOfInterest:true}` | the stage's reviewer | → `Draft` with `completionRequestReason`; after resubmission it returns to the stage that asked: the lawyer's request → `PendingLegalReview`; the admin's → `PendingIssuance` with the legal approval kept |
| `POST /{id}/reject` `{reason, noConflictOfInterest:true}` | the stage's reviewer | → `Rejected` (final). The owner may start a new request |

Grants: `GET /api/admin/marketplace/authority-grants`, `POST /api/admin/marketplace/authority-grants/{id}/revoke {expectedVersion}`.

Staff (`/api/admin/staff/legal-reviewers`, needs `platform.staff.manage`): `GET`, `POST {email}` (Active account with a
verified email; not yourself), `DELETE /{userId}`.

Drafts are invisible to staff (404) until submitted.

## 4. Documents: storage, validation and malware scanning

**Validation at upload.** At most 10 MB (the body is read with a hard cap, whatever the declared length). The type comes
from the bytes, never the name or header: PDF (`%PDF-`), JPEG, PNG, WebP. At most 10 documents in force per request and
20 versions per document. The stored name is generated (`{requestId}/{versionId}.pdf`); the client's file name is kept
only as a label, stripped of any path. A SHA-256 of the bytes is recorded.

**Quarantine.** A new version is written to `quarantine/...` in private storage with `scanStatus = PendingScan`. Nothing
under quarantine is ever served.

**Scanning.** A background worker (`DocumentScanWorker`, every `MalwareScanning:PollIntervalSeconds`) takes due versions
(row-locked with `FOR UPDATE SKIP LOCKED`, so several API instances can run it), re-reads the bytes and checks the SHA-256,
then streams them to ClamAV's clamd (`INSTREAM`):

| Answer | Result |
|---|---|
| `stream: OK` | copied to `documents/...`, quarantine copy deleted after the commit, `Clean` with engine and signature-database version |
| `stream: <signature> FOUND` | bytes deleted after the commit, `Rejected` with the signature (shown to staff) |
| anything else, unreachable, timeout, digest mismatch, missing file | retried with exponential backoff (`RetryBaseDelaySeconds` × 2ⁿ, capped at `RetryMaxDelayMinutes`); after `MaxAttempts` → `ScanFailed` |

`ScanFailed` is never clean: the owner can ask for a rescan or replace the file. With **no scanner configured**
(`MalwareScanning:ClamAvHost` empty) the worker does nothing at all; documents stay `PendingScan` and no request can be
submitted. `GET /health/scanner` reports Healthy (clamd answers), Unhealthy (clamd down) or Degraded (not configured).

**Downloads** stream from the API after the authorization check, with `X-Content-Type-Options: nosniff`,
`Cache-Control: private, no-store`, `Content-Security-Policy: default-src 'none'; sandbox`, and as an attachment.

**History.** Versions and decisions are never deleted (enforced by the DbContext and by foreign keys without cascade).
Replacing supersedes the previous version; removing marks the document removed. A document any of whose versions was
covered by `LegalApproved` or `AdminApproved` cannot be removed.

## 5. The final approval is atomic and re-checks everything

`POST /issue`, in one database transaction under the property lock:

1. request is `PendingIssuance`; the administrator holds `grants.issue`, is not the requester, not the legal reviewer,
   confirmed no conflict, and is independent (§6);
2. the legal reviewer **still** holds the legal-review permission and is still independent;
3. the property is Active, still registered by the requester, and **unchanged since the legal review**
   (`authority_requests.property_changed_since_review` otherwise: ask for completion so it is reviewed again);
4. the owner profile is still the approved one; no grant in force or expired-unrevoked blocks the purposes;
5. `missing` is empty again: every required document present and every document in force `Clean`;
6. no sale in force, no short stay in force for a sale, property not reserved;
7. one grant per purpose is issued, each listing is created from the stored content with the request's documents as
   evidence (exact version + SHA-256), submitted with their scan assessments, and approved and published.

Any refusal returns before the commit and nothing is half done.

**Substantial changes.** Everything the owner controls is frozen outside Draft. Any change goes through "ask for
completion": the request becomes a Draft, the owner edits, resubmits, and **both** reviews happen again. A property edit
after the legal review blocks the final approval (step 3). After publication, a listing change goes through the listing
submission review (`/api/admin/marketplace/listing-submissions`), with the same document evidence; new ownership
documents need a new authority request.

**Recorded for every step** (`decisions`): kind, actor, time, reason, the request version it produced, the reviewer's
no-conflict confirmation, and the exact document version ids in force.

## 6. Where each check comes from

| Check | Source | Simulated? |
|---|---|---|
| Owner's identity | Owner verification: national ID, address and proof, approved by an owner-verification reviewer | No |
| Ownership / authority to advertise | The legal reviewer's examination of the uploaded documents against the property, recorded as `LegalApproved` | No (human review) |
| Documents are free of malware | ClamAV verdict on the exact stored bytes (SHA-256 checked) | No |
| Evidence attached to the listing | `AuthorityDocumentEvidenceProvider`: the request's Clean document versions | No |
| Reviewer identity verified | Reviewer account Active with a verified email; submitter Active with an approved owner profile | No |
| Reviewer has no conflict | `RecordedRelationshipIndependenceProvider`: not the requester or the owner's account, shares no workspace with them; **plus** the reviewer's per-decision confirmation (`noConflictOfInterest`), recorded | Records + attestation |
| Organisation owners | Controlling persons are not recorded, so independence is unknown and every decision is refused | — |

What the platform cannot know by itself: whether the deed is genuine at the land registry, and private relationships
between a reviewer and a principal who has no account. The first is the legal reviewer's professional responsibility
(no government registry API is integrated); the second is covered by the recorded attestation. If an external
verification service (land registry, national identity) becomes available, it plugs in behind
`IReviewerIndependenceProvider` / a new legal-verification step; nothing else changes.

## 7. Deployment and configuration

`docker-compose.yml` now runs:

- `clamav` (`clamav/clamav:stable`, not published to the host, signatures in the `clamav_db` volume,
  `StreamMaxLength=32M`). The first start downloads signatures (several minutes); needs about 1.5–3 GB RAM. freshclam
  keeps signatures updated inside the container;
- `api` with `DocumentStorage__RootPath=/data/documents` on the named volume `documents_data`, and
  `MalwareScanning__ClamAvHost=clamav`.

| Setting | Default | Meaning |
|---|---|---|
| `DocumentStorage:RootPath` | `App_Data/documents` (compose: `/data/documents`) | Private document root. Must differ from `ImageStorage:RootPath` (checked at start) |
| `MalwareScanning:ClamAvHost` / `ClamAvPort` | empty / 3310 | clamd. Empty = no scanning, nothing becomes Clean |
| `MalwareScanning:TimeoutSeconds` | 120 | Per scan |
| `MalwareScanning:WorkerEnabled` | true | Run the worker on this instance |
| `MalwareScanning:PollIntervalSeconds`, `MaxAttempts`, `RetryBaseDelaySeconds`, `RetryMaxDelayMinutes`, `BatchSize` | 10, 5, 30, 30, 20 | Queue and retry policy |
| `StaffRoles:ProvisionOnStartup` | true | Provision the Legal Reviewer role and the administrator's permissions at start (idempotent, under the bootstrap's advisory lock) |

Migration `AuthorityDocumentsScanningAndDecisions` adds document versions and scan state, the decision table, the
completion/rejection split and the reviewed-property marker. Rows written before it: documents become version 1 with
`ScanFailed` ("uploaded before malware scanning existed", all-zero digest placeholder) and must be replaced; "sent back"
requests become completion requests; a pending final approval is refused until the request is reviewed again.

First deployment:

1. `docker compose up -d --build` (migrator runs first; then `api`, `clamav`).
2. Bootstrap the first Platform Admin as before (`BOOTSTRAP_ENABLED=true`, `BOOTSTRAP_PLATFORM_ADMIN_EMAIL`), restart once;
   the catalogue then completes the role. Turn the bootstrap off again.
3. The administrator appoints legal reviewers with `POST /api/admin/staff/legal-reviewers`.
4. Wait for `GET /health/scanner` to be Healthy before announcing uploads (uploads before that simply wait in quarantine).

## 8. Durable storage, backup and restore

The database and the document volume are **one unit**: a document row without its file cannot be downloaded or approved
(the scanner and the evidence check verify the SHA-256), and a file without a row is never served. Back them up together.

**Backup** (daily at least; keep copies off the server, encrypted, with restricted access — these are identity and
ownership documents):

```bash
# 1. Database (consistent snapshot)
docker compose exec -T postgres pg_dump -U smart_property -Fc smart_property > backup/db-$(date +%F).dump
# 2. Documents, right after the database dump (files are write-once; a later file is harmless, a missing one is not)
docker run --rm -v smartpropertyback_documents_data:/data:ro -v "$PWD/backup":/backup alpine \
  tar czf /backup/documents-$(date +%F).tar.gz -C /data .
```

The volume name is `<compose project>_documents_data` (`docker volume ls`). Photos (`IMAGE_STORAGE_HOST_PATH`) are backed
up the same way. `clamav_db` need not be backed up (it is re-downloaded).

**Restore** (stop the API first so nothing is written meanwhile):

```bash
docker compose stop api
docker compose exec -T postgres pg_restore -U smart_property -d smart_property --clean --if-exists < backup/db-DATE.dump
docker run --rm -v smartpropertyback_documents_data:/data -v "$PWD/backup":/backup alpine \
  sh -c "rm -rf /data/* && tar xzf /backup/documents-DATE.tar.gz -C /data && chown -R 1654:1654 /data"
docker compose up -d api
```

(`1654` is the `APP_UID` of the .NET runtime image.) After a restore, check one document per state: download a Clean
document as its owner and as a reviewer. If a file is missing, its version cannot be downloaded (404) and a pending
request cannot be approved; ask the owner to replace it.

**Moving to object storage later.** `IPrivateDocumentStorage` is the only seam: an S3/Azure Blob implementation with a
private bucket (no public ACL, server-side encryption, versioning on) replaces `LocalDiskPrivateDocumentStorage` without
other changes.

## 9. Verification status

- Unit tests (Domain): versions, scan transitions and retries, approval locks, history, attestation, separation of duties.
- API integration tests (`AuthorityRequestWorkflowTests`): separate owner, legal reviewer, administrator and customer
  accounts over HTTP with the production evidence and independence providers, the real staff catalogue and staff API;
  infected files, scanner outage and `ScanFailed`/rescan, changed bytes, unauthorized access, replacement, completion
  requests, rejection, property change after review, shared-workspace conflict, and refusal of publication before both
  approvals. The scanner there is a scripted stand-in answering by content.
- `ClamAvMalwareScannerTests`: the clamd protocol client against a local fake clamd (framing, OK, FOUND, ERROR, unreachable).
- **Live check, 2026-11-12**, on an isolated compose project (`sp-verify`: own volumes, port 5099, image tag `verify`;
  real PostgreSQL migrated from empty, the API container in Staging, `clamav/clamav:stable` = ClamAV 1.5.4):
  - first boot: bootstrap created Platform Admin, then the catalogue attached the 5 administrator permissions;
  - 35 checks, 0 failures: appointment of the legal reviewer by the administrator; owner profile approved; property;
    request; a clean deed (PendingScan → Clean, moved to `documents/`, bytes downloaded identical) and a PDF carrying
    the EICAR test file (**`Rejected`, `Eicar-Signature`, bytes deleted**); submission refused while scanning and while
    a document was infected; customer 404/403 everywhere; administrator refused before the legal approval; owner cannot
    review; legal approval refused without the no-conflict confirmation; nothing listed after the legal approval;
    lawyer cannot give the final approval; the administrator's approval published the listing and the customer could
    open it; history `Submitted > LegalApproved > AdminApproved`;
  - outage: with clamd stopped `/health/scanner` was Unhealthy and the document stayed `PendingScan` with a retry
    (`Scanner unreachable`), download 409; after clamd restarted the retry marked it `Clean` and download returned 200.
  - The SQL used there only seeded what a fresh database lacks (one country/city/area) and marked the four test emails
    verified (no SMTP in that stack); every other step went through the API.
- The local unit and API test runs on 2026-11-12 were blocked by Windows Application Control (0x800711C7) on the freshly
  built test DLLs; they must be run once the policy allows them:
  `dotnet test tests/SmartProperty.UnitTests`, then
  `dotnet test tests/SmartProperty.Api.IntegrationTests --filter "FullyQualifiedName~AuthorityRequestWorkflowTests|FullyQualifiedName~ClamAvMalwareScannerTests"`,
  then the full API and persistence suites (one after the other, not in parallel).
