# Property images

A property has an ordered group of photos. The first is the cover.

## Endpoints

Route: `/api/properties/{propertyId}/images`

| Method | Path | Who | What |
|---|---|---|---|
| `POST` | `/` | `property.update` | Upload files (`multipart/form-data`, field `files`). Returns `201` with the added images. |
| `PUT` | `/order` | `property.update` | Body `{ "imageIds": [...] }`: every image id, once. The first becomes the cover. |
| `DELETE` | `/{imageId}` | `property.update` | `204`, also when the image is already gone. |
| `GET` | `/{imageId}/file` | anyone | The photo itself. |

There is no list endpoint: `images` is part of every property response (get, list, update, create, favorites):

```json
"images": [
  { "id": "…", "url": "/api/properties/{id}/images/{imageId}/file", "fileName": "front.jpg",
    "contentType": "image/jpeg", "sizeBytes": 183422, "sortOrder": 0, "isCover": true }
]
```

Use `url` directly as an `<img src>` (prefix with the API host).

## Rules

- Types: JPEG, PNG, WebP. The type is read from the file's bytes, not its name or declared content type.
- At most 5 MB per file, 10 files per request, 20 images per property.
- A request is all-or-nothing: one bad file rejects the whole upload and stores nothing.
- Validation failures are `422 validation.failed` with the file named in the message.
- An archived property cannot gain, lose or reorder images (`409 properties.archived`); unknown property is `404`.

## Storage

Files are kept on the server's disk under `ImageStorage:RootPath` (default `App_Data/images`, git-ignored). The database
holds only the key, name, type, size and position (`registry.property_images`). Keys are generated
(`properties/{propertyId}/{imageId}.ext`, so `Storage` holds one folder per kind of owner: `properties/` now, e.g. `users/` later), never taken from the client.

Docker: compose binds the repository `Storage` folder to `/data/images` and sets
`ImageStorage__RootPath=/data/images` (set `IMAGE_STORAGE_HOST_PATH` in `.env` to keep the photos in another host folder). Running locally in Development uses the same `Storage` folder. Without a mount the
photos are lost when the container is recreated. Back the volume up with the database.

## Things to know

- The file endpoint is public (an `<img>` tag cannot send a token). Ids are random GUIDs, so addresses cannot be guessed,
  but anyone holding a URL can view the photo. Tell me if photos must be private; that needs signed URLs or cookie auth.
- Responses are cacheable for a year (`immutable`, ETag, `304` supported); a changed photo is a new id.
- The AI search listing does not carry images; they are not synchronized.
- Deleting removes the row first and then the file; if the file cannot be deleted it is left as an orphan rather than
  failing the request.
