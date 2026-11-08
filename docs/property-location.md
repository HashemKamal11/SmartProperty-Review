# Property location: send the area, not text

When a property is added or updated, its location comes from the **places** (`/api/locations`), not from free text.
The frontend picks a country, city and area from the lists and sends only the area (and a compound, if the property is in
one). The server fills in the address from that choice.

## Request (`POST /api/properties`, `PUT /api/properties/{id}`)

| Field | Meaning |
|---|---|
| `areaId` | **Required.** The area picked from `GET /api/locations?level=Area&parentId={cityId}`. |
| `compoundId` | Optional. A compound inside that same area. |
| `addressLine`, `postalCode`, `latitude`, `longitude` | Still sent by the caller: the street line and the exact spot. |

`countryCode`, `city`, `region` and `district` are **no longer part of the request**. Sending them has no effect.

## What the server stores (and returns in `address`)

| Address field | Taken from |
|---|---|
| `countryCode` | the area's country (two letters, e.g. `SA`, `EG`) |
| `city` | the area's city, English name |
| `district` | the area, English name |
| `region` | not a level of the places, so it stays empty |

The Arabic names stay available through the property's `place` object in every response.

## Errors (`422 validation.failed`)

- `areaId` missing or empty: "Area is required: choose the property's area from the locations list."
- Unknown `areaId` or `compoundId`, or a compound that is not in that area.
- The area's country has no country code yet: "The country '<name>' has no country code yet, so a property cannot be
  placed in it." This is for a country the code table does not know; set its code (`locations.countries.code`).

## Where the country code comes from

Countries now carry an ISO code (`locations.countries.code`). The migration fills it for the countries already stored,
and a country synced later gets it from the same lookup by English name
([CountryCodes.cs](../src/Core/SmartProperty.Domain/Locations/CountryCodes.cs)). A name not in the table is stored without a code.
