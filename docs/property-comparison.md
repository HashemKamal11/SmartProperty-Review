# Property comparison

A signed-in user, buying or renting, puts two or three properties side by side. The list is saved per user, so it
survives a refresh and moves between devices. No permission is needed: the list is the user's own, and the user comes
from the access token, never from the route.

## Rules

- At most **3** properties (`comparison.limit_reached`, 409).
- At least **2** to compare: `canCompare` is true from two properties on. The table is built on the frontend from the
  items; below two, show the cards only.
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
      "property": { "...": "the same object the property endpoints return (type, place, area, bedrooms, ...)" },
      "listings": [ { "...": "each advertisement visible for it now: purpose, price, pricing options, title" } ]
    }
  ],
  "maxItems": 3,
  "minItemsToCompare": 2,
  "canCompare": true
}
```

Items come in the order they were added. Each `property` is the full physical property, so every row of the table (type,
location, area, bedrooms, year built, finishing status, bathrooms, top features from the amenities and views) and the
card (image, rooms) is read from it; the price and the title come from `listings`.

## The match percentage

`matchPercentage` is the user's **latest AI match** for that property (from the AI search). When the AI never gave one
for that user and property, it is `null` (and `matchDescription` is `null`): show "the AI gave no value" instead of 0.
No AI call is made by this endpoint.

## Prices live on listings, not on the property

A property no longer has a price, a transaction type or a listing status: those belong to its **advertisements**
(marketplace listings, one per purpose). Each item therefore carries `listings`: the advertisements a visitor can see for
that property right now, with the same shape as the public marketplace summary (`purpose`, `title`, `price`, and for a
rental every `pricingOptions` entry: Daily, Weekly, Monthly or Yearly, each with its own amount and currency). A property
with no visible advertisement has an empty list, and a property can be compared whether or not it is advertised. Because a
sale price and a rent price are no longer one number on the property, there is no sale/rent mismatch rule any more; the
screen compares the listings it is shown.
