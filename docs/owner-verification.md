# Owner verification

A signed-in user can buy or rent without any approval. To **add properties** they must first complete an owner profile
and have a platform reviewer approve it.

## Flow

```
NotStarted -> Draft -> UnderReview -> Approved
                ^           |
                |           +-> Rejected (with a reason)
                +-- editing a Rejected or Approved profile returns it to Draft
```

| Status | Meaning | User may edit? | May add properties? |
|---|---|---|---|
| `NotStarted` | Nothing saved yet (not stored) | yes | no |
| `Draft` | Being filled in or corrected | yes | no |
| `UnderReview` | Submitted, waiting for a reviewer | **no** (409) | no |
| `Approved` | A reviewer approved it | yes, but any edit returns it to `Draft` and withdraws the right | yes |
| `Rejected` | Rejected; `rejectionReason` says why | yes | no |

## The three steps (the screens)

1. **Account data** (name, email, phone, verification flags): read from the account, never edited here.
2. **National ID**: `PUT /api/owner-profile/identity`
   `{ idCountryCode, nationalId, nationality, birthDate, idExpiryDate, confirmed }`
   `idCountryCode` is the country that issued the ID (case-insensitive); spaces and hyphens in `nationalId` are ignored:

   | `idCountryCode` | Document | Format |
   |---|---|---|
   | `SA` | Saudi national ID or Iqama | 10 digits, starting with 1 (citizen) or 2 (resident) |
   | `EG` | Egyptian national ID | 14 digits: century (2 = 1900s, 3 = 2000s), then `YYMMDD`, which must equal `birthDate` |
   | `AE` | Emirates ID | 15 digits starting with 784 (e.g. `784-1990-1234567-1`) |

   Only the format is checked: check digits are not verified and no issuing authority is consulted. `nationality` is
   free text and separate (an Egyptian living in Saudi Arabia uses `SA` with an Iqama). The owner must be 18 or older,
   the ID must not be expired, and `confirmed` must be true. A national ID of a country can belong to one account only
   (`409 owner_profile.national_id_taken`). The response's `identity` includes `idCountryCode`.
3. **National address**: `PUT /api/owner-profile/address`
   `{ buildingNumber (1 to 10 digits), streetName, district, city, postalCode (5 digits), additionalNumber (optional; 1 to 10 digits when given), confirmed }`
   `additionalNumber` exists only in the Saudi national address; send `null` or leave it out elsewhere.
   then `PUT /api/owner-profile/address-proof` as `multipart/form-data`, field `file` (JPEG, PNG or WebP, at most 10 MB).

`GET /api/owner-profile` returns the whole profile with `status`, `canAddProperties`, each step (or `null`),
`rejectionReason`, and `address.hasProofImage`. `GET /api/owner-profile/address-proof` returns the user's own image.
`POST /api/owner-profile/submit` sends it for review (`422` when a step or the image is missing).

Validation problems are `422 validation.failed` with a readable message.

## Review (platform admins)

Requires the permission `owner.verification.review` (given to the Platform Admin role by bootstrap).

| Endpoint | Purpose |
|---|---|
| `GET /api/admin/owner-verifications?status=UnderReview&page=1&pageSize=20` | The queue. Pending ones come oldest first. Drafts are never listed. The list carries no ID number. |
| `GET /api/admin/owner-verifications/{id}` | All three steps. |
| `GET /api/admin/owner-verifications/{id}/address-proof` | The proof image (never cached). |
| `POST /api/admin/owner-verifications/{id}/approve` | Approve. |
| `POST /api/admin/owner-verifications/{id}/reject` `{ reason }` | Reject. A reason is required and the owner sees it. |

A reviewer cannot decide their own profile (`403 owner_profile.cannot_review_own`).

## What approval unlocks

Approving assigns the platform role **Property Owner**, created on first use. It holds only `property.create` and
`property.read`, so the existing permission check on `POST /api/properties` is the single gate. An owner cannot update
or archive properties, and cannot review anything. Editing an approved profile removes the role again until it is
re-approved.

`POST /api/properties` now records the creating user as `owner_user_id` on the property. It is stored but not yet
returned by any endpoint.
