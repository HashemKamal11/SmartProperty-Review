# Marketplace: listings, requests and commitments

A **property** is the physical thing in the registry: its place, type, rooms, area, amenities, views, images and time
zone. It has no price, no transaction type and no availability. Everything commercial lives on **listings**.

This page is the working description of the marketplace API. The decisions behind it and the progress log are in
`Docs/Property-Listing-Separation-Design.md`.

> **Advertising authority is now a real workflow.** Who may advertise is decided by an authority request: the owner's
> documents are malware-scanned by ClamAV, a legal reviewer approves, and a different platform administrator gives the
> final approval, which issues the grants and publishes the listings atomically. Evidence is the owner's scanned
> documents; reviewer independence comes from the recorded relationships plus a recorded no-conflict confirmation. See
> [authority-documents-and-review.md](authority-documents-and-review.md). Organisation owners remain unsupported
> (independence unknown, refused).

## The model

| Thing | What it is |
|---|---|
| Property | Physical facts only. Has a time zone (falls back to its country's). |
| Listing | One advertisement of one property for **one purpose**: Sale or Rent. A property has at most one Sale listing and one Rent listing. "For both" is two listings, never a third kind. |
| Submission | One version of a listing's content. Versions are immutable once submitted; a new version is a new submission. A reviewer approves one, and that approval publishes it. |
| Grant | An administrator-issued, purpose-specific permission for one user to advertise one property for one purpose. Required to create, submit, publish, accept. |
| Request | A customer's purchase offer or dated rental request, against the approved version of a published listing. |
| Commitment | An accepted request: a sale, or a rental for dates. |

Sale price: `amount` and `currency`. Rent price: any of **Daily, Weekly, Monthly, Yearly**, each with its own amount and
currency (`pricingOptions`), separate from how long anyone stays. A rental also carries `rentalTerms`: check-in and
check-out time, the availability windows, an optional minimum stay, all in the property's time zone. The primary option
(Monthly, then Yearly, Weekly, Daily) is what a single "price" means where one number is needed.

## Advertiser workflow

1. `POST /api/marketplace/me/listing-submissions` creates a draft for one property and purpose, or
   `POST /api/marketplace/me/properties/{propertyId}/listing-drafts` creates the Sale draft, the Rent draft or both in one
   unit of work. **Both needs a valid Sale grant and a valid Rent grant**; if either is missing nothing is written.
2. `PUT .../listing-submissions/{id}` edits a never-submitted draft (`expectedRevision` required);
   `DELETE .../{id}?expectedRevision=` discards it. Neither can touch a submitted version.
3. `POST .../{id}/submit` sends the draft for review. It does not publish.
4. An administrator approves or rejects (`POST /api/admin/marketplace/listing-submissions/{id}/approve|reject`). Approval
   publishes; the previous published version becomes Superseded in the same commit.
5. `POST /api/marketplace/me/listings/{listingId}/withdraw` takes the advertiser's own published listing off the
   marketplace. It works even if the grant has since lapsed: withdrawing only reduces exposure.
6. **Advertising again** is explicit: create a new draft for the same property and purpose, submit it, have it approved. A
   withdrawn or closed listing is never republished from anything older than the withdrawal.

All of it takes the acting user from the access token. No request carries a user, owner or authority.

## Customer workflow

- `POST /api/marketplace/purchase-requests` `{ listingId, offeredAmount?, message? }`: the offer defaults to the
  listing's price and is in the listing's currency.
- `POST /api/marketplace/rental-requests` `{ listingId, pricingPeriod, startDate, endDate, message? }`: **the price is not
  sent**. It is copied from the chosen pricing option. The stay is `[startDate, endDate)`, the end date being the check-out
  day, so another stay may begin on it. It must lie inside one availability window, meet the minimum stay, start no earlier
  than today **in the property's time zone**, and not overlap a commitment in force.
- `GET /api/marketplace/me/requests`, `GET .../me/purchase-requests/{id}`, `GET .../me/rental-requests/{id}`: only your own.
- `POST .../me/purchase-requests/{id}/withdraw`, `POST .../me/rental-requests/{id}/withdraw`: only while open.

A request can only be made against a published, open listing whose advertiser **still holds a valid grant**, on the version
it publishes now. You cannot request your own listing. One open request per customer and listing; once it is decided or
withdrawn you may request again (repeat rentals).

## Owner workflow (receiving requests)

`GET /api/marketplace/owner/requests` (filter by `kind`, `listingId`, `status`) lists requests made against versions you
authored, with the requester's name. Then accept or reject:
`POST .../owner/{purchase|rental}-requests/{id}/accept|reject`.

**Accepting** needs your grant for the purpose to be valid now and the request to be against the version the listing
publishes now. In one transaction it:

1. creates the **commitment**, with a signing deadline fixed from the administrator's signing window at that moment;
2. **closes the property's published Sale and Rent advertisements** (the owner advertises again only by an explicit new
   submission and review);
3. closes **every other open request**, of either kind, as `ClosedListingUnavailable`, keeping their history;
4. never touches a commitment already in force. An approved booking or a signed agreement is not cancelled by a later
   acceptance.

### Rules between commitments

| | Accept a rental | Accept a sale | Publish a Sale ad | Publish a Rent ad |
|---|---|---|---|---|
| a **sale** is in force | refused | refused | refused | refused |
| a **Daily or Weekly** rental is in force | only for non-overlapping dates | **refused** (short stay = occupancy) | **refused** | allowed |
| a **Monthly or Yearly** rental is in force | only for non-overlapping dates | **allowed** (the sale preserves the rental) | allowed | allowed |

"In force" means pending signature and not past its deadline, signed, or occupied. Rental dates are half-open, so
back-to-back stays are fine. Overlap and a second active sale are also refused **by the database itself** (exclusion
constraints, deferred to commit), so two simultaneous acceptances cannot both win.

## Commitments

`GET /api/marketplace/me/commitments?role=Customer|Owner`, `GET .../{id}`: visible to the customer and the owner only.

| Status | Meaning |
|---|---|
| `PendingSignature` | Accepted; waiting for the owner to confirm the agreement was signed, until the deadline. |
| `Signed` | Signed. **Never cancelled or expired by the platform.** |
| `Occupied` | (Rental) the tenant took possession. The dates stay blocked, and a short stay still blocks selling, **until the owner confirms the return**. Nothing infers the end of an occupancy from the calendar. |
| `Completed` | A rental was returned, or a sale was handed over. Releases the dates. |
| `Expired` | Not signed before the deadline. Releases the dates; the history stays. |

The owner moves it forward with `POST .../{id}/confirm-signed`, `confirm-handover`, `confirm-return`, in that order. These
are the owner's statements, nothing more: no document, signature, payment or ownership transfer is involved.

**No background job.** An unsigned commitment past its deadline stops counting immediately (reads and checks ignore it) and is
recorded as `Expired` the next time anything writes to that property, in the same transaction. A reader sees `Expired`
even before that.

**Signing window.** `GET|PUT /api/admin/marketplace/settings/signing-window` (`{ "hours": 1..720 }`, default 72). It needs the
Platform-scoped `marketplace.settings.manage` permission, which is **defined but not yet created or assigned by the platform
administrator bootstrap**; give it to an administrator explicitly. A change applies to later acceptances; a deadline already
given never moves.

## Public reads

`GET /api/public/marketplace/listings` and `GET .../{listingId}` show only listings that are published and open, whose
approved version is the one published, on an active property. Closing a listing (withdrawal, or an accepted request) removes it
at once.

- A rental's detail adds `pricingOptions`, `rentalTerms` (time zone, check-in/out, `windows`, `minimumStayDays`) and
  `blockedRanges` (dates held by a commitment, no identities).
- Search adds `availableFrom` + `availableTo` (Rent only, both required): the stay must fit one window and overlap no
  held dates. `rentPeriod` finds a rental by **any** pricing period it offers, and with `minPrice`/`maxPrice`/`currency`/
  price sort the price **of that period** is the one compared and shown.
- Favorites and comparison items carry `listings`: the advertisements visible for that property.

## The listing's location, and the city fallback

A listing freezes a location snapshot (country, region, city, district) when the draft is written, and the public and review
reads show it. The place tree that creates a property has only country, city and area, so a property made through the API has
**no `address.region`** (it stays `null`). Without a rule, every such property would be refused as unavailable.

The rule, in `ListingSubmissionWriteRepository.LockPropertyAsync`:

- the listing's region label is the property's recorded `address_region` when it is not blank;
- otherwise it is the property's own recorded `address_city`;
- if neither exists the property is refused (`listing_submissions.property_unavailable`). The district, the country and any text
  guess are **never** substituted for a missing value, and a blank region counts as missing.

What it does **not** do: it never writes to the property (the stored region stays `null`; a test compares the whole address and
`updated_at` before and after publishing), it invents no new fact (the label is a value already on the property), and it does
not hide itself: for these properties the published `location.region` equals the city (for example `Cairo`). It is a
presentation label for the frozen snapshot, not a claim that the city is a governorate. If a real region is recorded later, the
snapshot no longer matches the property, so a draft written before is refused as stale (`cannot_publish` at approval, or
`property_changed` at submission) instead of being published with the old label, and a new version carries the real region.
A cleaner long-term fix is a nullable region in the listing contract or collecting a real region when properties are created;
neither is done.

## Errors worth knowing

| Code | HTTP | When |
|---|---|---|
| `listing_submissions.not_eligible` | 403 | No valid grant for that purpose (also: unknown property; the two are not told apart). |
| `listing_submissions.evidence_verification_unavailable` | 503 | Attaching evidence, submitting or approving while no evidence provider can answer. |
| `listing_submissions.independence_unverifiable` | 403 | A decision while no reviewer-independence provider can answer. |
| `listing_submissions.property_committed` / `short_stay_occupancy` | 409 | Publishing against the rules table above. |
| `marketplace_requests.listing_unavailable` | 404 | Missing, unpublished, closed, wrong purpose, or its advertiser lost the grant. |
| `marketplace_requests.dates_unavailable` | 409 | The dates overlap a commitment in force. |
| `marketplace_requests.already_requested` | 409 | You already have an open request on the listing. |
| `marketplace_requests.request_outdated` | 409 | The listing was approved again since the request; ask the customer to request again. |
| `marketplace_commitments.step_not_allowed` | 409 | A confirmation out of order, or signing after the deadline. |

## Development demo mode (not a production feature)

`docker-compose.dev-demo.yml` runs the API as Development with demo mode on, so the walkthrough can be run end to end:
`docker compose -f docker-compose.yml -f docker-compose.dev-demo.yml up -d --build --no-deps api`. Demo mode needs the
Development environment **and** `DemoMode__Enabled=true`; with the flag in any other environment the host refuses to start. It
replaces three providers with labelled simulations (evidence assessment for fixtures registered through
`POST /api/development/evidence`, reviewer relationship checks with a simulated identity fact, and the grant legal-review
reference `SIMULATED-DEV-LEGAL-REVIEW`). `tools/SmartProperty.DevTools` provisions the Development roles and grants, and
`tools/dev-flow/Run-DevFlow.ps1` runs the whole flow with PASS/FAIL checks. Nothing here replaces the integrations below.

## What is not finished

- **Grant issuance.** There is no endpoint to record a legal review, issue or revoke a grant. Grants are read, never
  created, by this code, and the tests seed them through the Domain. Nothing is automatic. Until an issuing workflow exists
  no real user can advertise. (The Development command `issue-grants` stands in for it on development databases only, with a
  simulated legal-review reference. It is not an integration.)
- **Evidence verification.** The registered provider can never answer. A draft can be saved without evidence; attaching
  evidence, submitting and publishing return the 503 above. Evidence is never marked clean without a scan.
- **Reviewer independence.** The registered provider establishes nothing, so every approval and rejection is refused as
  unverifiable. Independence of an organisation owner's representatives cannot be established at all without a registry of
  legal parties. External identity verification does not exist; demo mode simulates that one fact and labels it.
- **Legacy reservations** (the old permanent, undated reservation) are left exactly as they are and still hide the
  property from the marketplace. They are **not** converted to commitments, because that would mean inventing end dates.
  Resolving one is an administrator task that does not exist yet.
- Out of scope by agreement: payments, full lease and signature workflows, ownership transfer, automated overstay handling,
  visits, notification delivery.
