# Favorite properties

A signed-in user can keep a list of favorite properties (the heart on a property card).

The list belongs to the user the access token names. No user id appears in any route or body, so one user can never read or
change another's list. No permission is needed: every signed-in user keeps their own.

## Endpoints

| Endpoint | What it does |
|---|---|
| `PUT /api/favorites/{propertyId}` | Marks a property. **`201`** when it was added, **`200`** when it already was a favorite (same body, original `favoritedAt`). |
| `DELETE /api/favorites/{propertyId}` | Unmarks it. **`204`** whether or not it was a favorite. |
| `GET /api/favorites?page=&pageSize=` | One page of the user's favorites, **most recently added first**. Each item is `{ favoritedAt, property, listings }` where `property` is exactly what `GET /api/properties/{id}` returns and `listings` are the advertisements currently visible for it (the price, pricing options and terms live there, not on the property); a property nobody is advertising has an empty list. |
| `GET /api/favorites/ids` | Just the property ids, to mark the hearts on a list of properties without loading every favorite. |

Paging works like every other list: `page` defaults to 1, `pageSize` to 10, out-of-range values are clamped.

## Rules

- An **unknown property** is `404 properties.not_found`.
- An **archived property cannot be added**: `409 favorites.property_archived`. One that is already a favorite **stays in the
  list** when it is archived, so the user can still see it (`status: "Archived"`) and remove it.
- Marking twice is the same favorite; two requests racing to mark the same property both succeed.
- A token for a user that no longer exists is `404 users.not_found`.

## Storage

`registry.favorite_properties (user_id, property_id, created_at)`; the primary key is the pair. Both ends are foreign keys
that do not cascade: users are deactivated and properties archived, never deleted, so a delete that would orphan a favorite
is refused.
