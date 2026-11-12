# Arabic and English Responses

Every endpoint can answer in English or Arabic. The client says which language it wants. The response keeps all of
its existing fields and adds `display<Field>` fields holding the text to show in that language.

## Choosing the language

| Priority | How | Example |
| --- | --- | --- |
| 1 | `lang` query parameter | `GET /api/amenities?lang=ar` |
| 2 | `Accept-Language` header | `Accept-Language: ar` or `ar-EG,ar;q=0.9,en;q=0.8` |
| 3 | Default | English |

- Supported values are `ar` and `en`. Regional variants fall back to their language (`ar-EG` → `ar`, `en-GB` → `en`).
- Anything else (`fr`, an empty value) is ignored, and the next rule applies.
- Every response states the language used in `Content-Language: ar|en` and sends `Vary: Accept-Language`.
- The language changes display text only. Numbers, dates, identifiers, codes and enum values stay the same in both
  languages, so requests are always sent in the same (English) vocabulary: `"type": "Apartment"`, never `"شقة"`.

## What is added to responses

The existing fields never change. Display fields are added next to them:

| Source fields | Added field | Content |
| --- | --- | --- |
| `xEn` + `xAr` (for example `nameEn`/`nameAr`, `titleEn`/`titleAr`, `descriptionEn`/`descriptionAr`) | `displayX` (`displayName`, `displayTitle`, `displayDescription`) | The value in the requested language; the other language when that one is empty |
| `x` + `xAr` (AI search results: `city`/`cityAr`, `propertyTypeAr`, `transactionTypeAr`, ...) | `displayX` | Same rule |
| A closed-list value: a field named `...Status`, `...Type`, `...Kind`, `purpose`, `role`, `...Period`, `...Decision`, `...Outcome`, `state`, `step`, `level`, `scope`, `...Stage`, `requirement`, `capacity` | `displayX` | The label of the value (`"PendingReview"` → `"Pending review"` / `"قيد المراجعة"`) |
| A list of closed-list values (`purposes`, `missing`) | `displayPurposes`, `displayMissing` | One label per value, same order |
| Error body `message` | `displayMessage` | The error message in the requested language |

A display field is omitted exactly when its source field is omitted (for example `pricingPeriod` on a sale).

### Examples

`GET /api/amenities` with `Accept-Language: ar`:

```json
[
  { "code": "Balcony", "nameAr": "شرفة", "nameEn": "Balcony", "displayName": "شرفة" }
]
```

`POST /api/properties?lang=ar` (abridged response):

```json
{
  "id": "...",
  "type": "Apartment",
  "displayType": "شقة",
  "status": "Active",
  "displayStatus": "نشط",
  "finishingStatus": "SemiFinished",
  "displayFinishingStatus": "نصف تشطيب",
  "country": { "id": "...", "nameEn": "Egypt", "nameAr": "مصر", "displayName": "مصر" }
}
```

Error with `Accept-Language: ar`:

```json
{
  "code": "validation.failed",
  "message": "Email must not exceed 320 characters.",
  "displayMessage": "يجب ألا يتجاوز البريد الإلكتروني 320 حرفًا.",
  "status": 422,
  "fieldErrors": null,
  "correlationId": "..."
}
```

`code` stays the stable, machine-readable key; `message` stays English (logs, support). Show `displayMessage` to people.

### What is not translated

- Text that people type themselves (property titles and descriptions entered in one language, review notes,
  rejection reasons, request messages) is returned as written. Where the platform stores both languages (`titleAr`
  and `titleEn`), `displayTitle` picks the right one.
- A value with no label yet is shown as its words in English (`"SomethingNew"` → `"Something new"`).
- A message with no Arabic translation is shown in English in `displayMessage`.

## For backend developers

The behaviour lives in `src/Presentation/SmartProperty.Api/Infrastructure/Localization`:

| File | Role |
| --- | --- |
| `LocalizationServiceCollectionExtensions.cs` | Request localization (`?lang`, `Accept-Language`), `Content-Language`/`Vary` headers, and the JSON modifier for MVC, `WriteAsJsonAsync` and the OpenAPI document. Only the UI culture changes; formatting stays English. |
| `DisplayFieldsModifier.cs` | The System.Text.Json contract modifier that adds the `display*` fields to every `SmartProperty.*` type. |
| `DisplayValues.cs` | English and Arabic labels of closed-list values. |
| `DisplayMessages.cs` + `ArabicMessages.json` | Arabic messages, keyed by the English text or by the English interpolation template (`"Email must not exceed {EmailMaxLength} characters."`). Placeholder values are put back in; values that are themselves messages or closed-list values are translated too. |

When you add:

- **An enum or closed-list value** returned to clients: add its labels to `DisplayValues`. The test
  `EveryValueOfAnEnumClientsSeeHasAnEnglishAndArabicLabel` fails until you do.
- **An error or validation message**: add the English text (or template, with the same `{placeholders}` as the C#
  string) and its Arabic version to `ArabicMessages.json`.
- **A response field the naming rules miss**: mark it `[property: LocalizedValue]` (closed-list value) or
  `[property: LocalizedMessage]` (message). Mark a field `[property: NotLocalized]` to stop a display field being
  added. The attributes are in `SmartProperty.Common.Localization`, so Application views can use them too.
- **Bilingual stored data**: name the columns `XEn`/`XAr` and `displayX` appears by itself.
