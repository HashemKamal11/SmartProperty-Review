# Property comparison

A signed-in user, buying or renting, puts two or three properties side by side. The list is saved per user, so it
survives a refresh and moves between devices. No permission is needed: the list is the user's own, and the user comes
from the access token, never from the route.

## Rules

- At most **3** properties (`comparison.limit_reached`, 409).
- At least **2** to compare: `canCompare` is true from two properties on. The table is built on the frontend from the
  items; below two, show the cards only.
- **All for sale or all for rent** (`comparison.transaction_type_mismatch`, 409): prices of a sale and a rent cannot
  be set side by side. Once the list is empty again, either kind may be started.
- A property that is for neither (no transaction type) cannot be added (`comparison.property_not_listed`, 409).
- An archived property cannot be added (`comparison.property_archived`, 409); one already on the list stays and is shown.
- An unknown property is `404`.
- The cap is enforced by the database too (positions 1 to 3, one row per position), so two requests racing cannot
  produce a fourth. The loser gets `409 comparison.concurrent_change` and may retry.

## Endpoints

| Endpoint | Result |
|---|---|
| `GET /api/comparison` | The whole list. |
| `PUT /api/comparison/{propertyId}` | Adds a property. `201` when added, `200` when it was already there. The body is the whole list either way. |
| `DELETE /api/comparison/{propertyId}` | Removes a property. `200` with the remaining list, whether or not it was on it. |
| `DELETE /api/comparison` | Empties the list. `200` with the empty list. |

## Response

```json
{
  "items": [
    {
      "position": 1,
      "addedAt": "2026-11-05T13:00:00Z",
      "matchPercentage": 96,
      "matchDescription": "...",
      "property": { "...": "the same object the property endpoints return (price, type, place, area, bedrooms, ...)" }
    }
  ],
  "maxItems": 3,
  "minItemsToCompare": 2,
  "canCompare": true,
  "transactionType": "Sale"
}
```

Items come in the order they were added. Each `property` is the full property, so every row of the table (price, type,
location, area, bedrooms, year built, finishing status, bathrooms, top features from the amenities and views) and the
card (image, title, rooms, price) is read from it.

## The match percentage

`matchPercentage` is the user's **latest AI match** for that property (from the AI search). When the AI never gave one
for that user and property, it is `null` (and `matchDescription` is `null`): show "the AI gave no value" instead of 0.
No AI call is made by this endpoint.
