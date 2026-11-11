# AI search integration

The platform keeps its properties in the registry and its advertisements in the marketplace, and lets an external
**AI search service** (<https://ai-search.neurix.uk/docs>) answer free-text searches. This page describes how the two are
connected. Changes still to be agreed with the AI team are collected in
[ai-integration-pending-changes.md](ai-integration-pending-changes.md).

> **What changed.** The AI service holds *advertisements*, not properties. Its `external_id` for a record is now the
> platform's **listing id** (one record per published Sale or Rent listing), no longer the property id. A property has no
> price, transaction type or listing status any more; those belong to its listings. See
> [marketplace-listings-and-requests.md](marketplace-listings-and-requests.md).

## Configuration

| Setting | Environment variable | Default | Meaning |
|---|---|---|---|
| `AiSearch:Enabled` | `AI_SEARCH_ENABLED` (compose) | `false` | Master switch. While off nothing is sent. |
| `AiSearch:BaseUrl` | `AI_SEARCH_BASE_URL` | `https://ai-search.neurix.uk` | The AI service. |
| `AiSearch:ApiKey` | `AI_SEARCH_API_KEY` | empty | Sent as `x-api-key` when adding or removing records. Searching needs no key. |
| `AiSearch:TimeoutSeconds` | `AI_SEARCH_TIMEOUT_SECONDS` | `30` | A slower answer counts as a failed call. |

Set them in `.env` (local, git-ignored) or the deployment's secret store; `.env.example` lists them. The key is a secret
and must never be committed.

## Flow 1: an advertisement is sent to the AI service

The marketplace decides and commits first; the AI service is told afterwards, **best effort**:

| Event (committed) | Call |
|---|---|
| A reviewer approves a submission, so the listing is published | `PUT /v1/listings/{listingId}` (adds, or replaces the copy) |
| The advertiser withdraws the listing | `DELETE /v1/listings/{listingId}` |
| An accepted request closes the property's published advertisements | `DELETE /v1/listings/{listingId}` for each one closed |

The record is built from the physical property (place, type, rooms, area in m², floor, furnished, finishing status,
amenities, the first view) and the approved listing (purpose as `TransactionType`, the listing's primary price, title and
description). `ListingStatus` is always `Available`, because only a published, open listing is ever sent. A listing has one
title and one description, in whatever language its advertiser wrote; the same text is sent as both the Arabic and the
English field so the service finds it either way.

**It never fails a decision and nothing retries it.** If the service is switched off, down, or the property lacks something
the service requires (an area with the service's id, an area in square metres, bedroom and bathroom counts, at most 50 of
each), the copy is not updated and a warning is logged with the listing id and a fixed category, never the content. There
is no status stored on the property or the listing and no background job, so a missed update stays missed until that
listing is published or withdrawn again. This is a known limitation, not a hidden one.

The AI service has **no notion of currency, pricing period or availability**. It receives the listing's primary price (for
a rental with several pricing options, the monthly price first) as a bare number. Use the marketplace search
(`GET /api/public/marketplace/listings`) for price, period and availability filters.

### Choosing the place

The platform keeps its own place tree in four tables: `locations.countries`, `cities`, `areas` and `compounds`. Each row
has this platform's id and the AI service's id (`ai_id`), plus both names, and points to its parent. A property links
to its **area** (and its **compound**, when it is in one); the city and country come from the area. The AI service's
ids are used only when a record is sent to it.

The AI service owns the data, so the tables fill themselves **the first time a level is asked for**:

1. `GET /api/locations` — countries.
2. `GET /api/locations?level=city&parentId={countryId}`
3. `GET /api/locations?level=area&parentId={cityId}` — send the chosen id as `areaId`.
4. `GET /api/locations?level=compound&parentId={areaId}` — optional, send as `compoundId`.

Every id in these calls is **this platform's id**, as returned by the previous level. A level that is already stored is
answered from the database without calling the AI service, so browsing keeps working while it is down; only an empty
level triggers a fetch, and if the AI service cannot be reached at that moment the answer is `502` (or `503` when the
integration is off). A `parentId` that is not stored is `404 locations.parent_not_found`.

A property's `place` in every response shows its country, city, area, and compound with both names.

The stored places are never refreshed from the AI service; if it adds a place later, existing levels do not pick it up.
That is intentional for now (see [ai-integration-pending-changes.md](ai-integration-pending-changes.md)).

### Vocabulary

Property types, finishing statuses and views are the AI service's closed lists (`GET /v1/options`); the two purposes are
called Sale and Rent in both systems. The platform's enums use the same names, written without spaces; `AiVocabulary`
converts them (`TwinHouse` → `Twin House`, `CoreAndShell` → `Core & Shell`, …).

Amenities have their own codes in the platform. Each one that has an equivalent in the AI service carries its
`ai_code` (`SwimmingPool` → `pool`); the others are simply not sent. Several platform amenities may share one AI code.

## Flow 2: a user searches

`POST /api/property-search` with `{ "query": "...", "topK": 3 }` (signed-in user; `topK` defaults to 3, at most 10).

1. The query goes to the AI service (`POST /v1/search`).
2. Each result has two ids: `property_id`, the AI service's **own** reference, and `external_id`, the id **this platform
   sent** when it added the record, which is our **listing id**. Only `external_id` says a result is ours; `property_id`
   is never taken for one of our ids.
3. A result is ours only if its `external_id` is the id of a listing that is **visible right now** (published, open,
   approved, on an active property). It then maps to the property it advertises, and a match is stored for the user: the
   percentage, the description and the query.
4. All results are returned. A result that is not ours has a null `propertyId` and `listing`, is shown from `details`, and
   is not stored. That includes a record whose listing was withdrawn or closed since, and a record indexed before listings
   existed (its `external_id` is a property id): both map to nothing, so stale prices from an old index are never shown as
   ours. Every result carries `externalId` (ours, or null), `aiPropertyId` (the AI service's own reference) and, when it
   is ours, `listing` (the advertisement as the marketplace shows it now) and `property`.

`matchPercentage` is the AI relevance `score` × 100, clamped to 0–100. It ranks results against each other; it is not
a probability. `description` is the AI's Arabic description, falling back to English, then the title.

If the AI service is unreachable the search answers `502`; if the integration is disabled, `503`.

## Endpoints

| Endpoint | Who |
|---|---|
| `GET /api/locations` | any signed-in user |
| `GET /api/views`, `GET /api/amenities` | any signed-in user |
| `POST /api/property-search` | any signed-in user |
| `POST /api/property-matches` | the AI service, with `X-Api-Key` — **legacy**, kept until the integration is complete |

`POST /api/properties/{id}/ai-sync` no longer exists: a property is not sent to the AI service, its advertisements are.

**Re-indexing after a gap.** Because nothing retries, records that were never indexed (the AI service was off when the
listing was published) or are stale (indexed under a property id before listings existed) must be rebuilt. The supported
way today is to withdraw and publish the listing again. A bulk re-index tool is not built.
