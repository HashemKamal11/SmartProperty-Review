# AI search integration

The platform keeps its properties in the registry and lets an external **AI search service**
(<https://ai-search.neurix.uk/docs>) answer free-text searches. This page describes how the two are connected.
Changes still to be agreed with the AI team are collected in
[ai-integration-pending-changes.md](ai-integration-pending-changes.md).

## Configuration

| Setting | Environment variable | Default | Meaning |
|---|---|---|---|
| `AiSearch:Enabled` | `AI_SEARCH_ENABLED` (compose) | `false` | Master switch. While off nothing is sent and every property reports `NotSynced`. |
| `AiSearch:BaseUrl` | `AI_SEARCH_BASE_URL` | `https://ai-search.neurix.uk` | The AI service. |
| `AiSearch:ApiKey` | `AI_SEARCH_API_KEY` | empty | Sent as `x-api-key` when adding or removing properties. Searching needs no key. |
| `AiSearch:TimeoutSeconds` | `AI_SEARCH_TIMEOUT_SECONDS` | `30` | A slower answer counts as a failed call. |

Set them in `.env` (local, git-ignored) or the deployment's secret store; `.env.example` lists them. The key is a secret
and must never be committed.

## Flow 1: a property is sent to the AI service

When a property is **created**, **updated**, or **archived**, the platform saves it first and then calls the AI
service:

| Event | Call |
|---|---|
| created / updated | `PUT /v1/listings/{propertyId}` (adds, or replaces the copy) |
| archived | `DELETE /v1/listings/{propertyId}` |

The AI listing's `external_id` is the property's own id: the platform and the AI service each have their own id for a
property, and the id we send in the path (`PUT /v1/listings/{external_id}`) is the platform's. The call carries nothing else
but the body and the `x-api-key` header.

The call is **best effort**. An outage of the AI service never loses or rejects a registry write. The outcome is stored
on the property and returned as `aiSync`:

| `aiSync.status` | Meaning |
|---|---|
| `Synced` | The AI service holds the property as last saved. |
| `NotSynced` | Nothing is held. `error` says why: the integration is disabled, the property is incomplete, or it was archived. |
| `Failed` | The last attempt failed. `error` says why. |

A property can only be sent when it has a **transaction type, price, area (m²), place (`areaId`), bedrooms and
bathrooms**, and no more than 50 bedrooms or bathrooms. Otherwise it stays `NotSynced` with a message naming what is
missing. Saving the property again, or `POST /api/properties/{id}/ai-sync`, sends it again; both are safe to repeat.

### Choosing the place

The platform keeps its own place tree in four tables: `locations.countries`, `cities`, `areas` and `compounds`. Each row
has this platform's id and the AI service's id (`ai_id`), plus both names, and points to its parent. A property links
to its **area** (and its **compound**, when it is in one); the city and country come from the area. The AI service's
ids are used only when the property is sent to it.

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

Property types, finishing statuses, transaction types, listing statuses and views are the AI service's closed lists
(`GET /v1/options`). The platform's enums use the same names, written without spaces; `AiVocabulary` converts them
(`TwinHouse` → `Twin House`, `CoreAndShell` → `Core & Shell`, …).

Amenities have their own codes in the platform. Each one that has an equivalent in the AI service carries its
`ai_code` (`SwimmingPool` → `pool`); the others are simply not sent. Several platform amenities may share one AI code.

## Flow 2: a user searches

`POST /api/property-search` with `{ "query": "...", "topK": 3 }` (signed-in user; `topK` defaults to 3, at most 10).

1. The query goes to the AI service (`POST /v1/search`).
2. Each result has two ids: `property_id`, the AI service's **own** reference, and `external_id`, the id **this platform
   sent** when it added the listing, which is our property id. Only `external_id` says a result is ours; `property_id` is
   never taken for one of our ids.
3. For every result whose `external_id` is the id of one of our properties, a match is stored for the user: the
   percentage, the description and the query.
4. All results are returned. A result that is not one of ours (`external_id` is null) has a null `propertyId`, is shown
   from `details`, and is not stored. Every result carries `externalId` (ours, or null) and `aiPropertyId` (the AI
   service's own reference).

`matchPercentage` is the AI relevance `score` × 100, clamped to 0–100. It ranks results against each other; it is not
a probability. `description` is the AI's Arabic description, falling back to English, then the title.

If the AI service is unreachable the search answers `502`; if the integration is disabled, `503`.

## Endpoints added

| Endpoint | Who |
|---|---|
| `GET /api/locations` | any signed-in user |
| `GET /api/views`, `GET /api/amenities` | any signed-in user |
| `POST /api/property-search` | any signed-in user |
| `POST /api/properties/{id}/ai-sync` | `property.update` |
| `POST /api/property-matches` | the AI service, with `X-Api-Key` — **legacy**, kept until the integration is complete |
