# API Contract Standard

This document defines the shared HTTP contract baseline for SmartProperty Admin APIs, Compliance APIs, and frontend integration. It is implementation-neutral unless a convention must be enforced at the API boundary.

## 1. Identifier Convention

Primary domain and resource identifiers use `Guid` / UUID unless a future requirement explicitly requires another identifier type.

JSON represents identifiers as UUID strings.

Existing identifiers must not be refactored only to satisfy this convention.

## 2. Date/Time Convention

Backend timestamp semantics are UTC.

Use `DateTimeOffset` where timestamps are required. API timestamp representation is ISO 8601.

Example:

```json
"2026-09-09T09:15:32Z"
```

Use `DateOnly` for date-only business values where appropriate. API date-only representation is:

```json
"2026-09-09"
```

## 3. Pagination Contract

Collection endpoints use query parameters:

```text
?page=1&pageSize=20
```

`page` is one-based. `pageSize` follows the existing `PageParameters.MaxPageSize` rule.

Paginated responses use this shape:

```json
{
  "items": [],
  "page": 1,
  "pageSize": 20,
  "totalCount": 0,
  "totalPages": 0,
  "hasNext": false,
  "hasPrevious": false
}
```

The API adapts from the existing `PagedList<T>` foundation and does not introduce a competing pagination abstraction.

## 4. Search Convention

Use the query parameter name:

```text
search
```

Search semantics are endpoint-specific. Do not introduce a generic filtering DSL until a real endpoint requires it.

## 5. Sorting Convention

Use query parameter names:

```text
sortBy
sortDirection
```

`sortDirection` conceptually supports:

```text
asc
desc
```

Sortable fields are endpoint-specific. Do not introduce reflection-based sorting or a generic sorting engine without a concrete endpoint requirement.

## 6. Error Response Contract

Errors use one standard JSON shape:

```json
{
  "code": "users.email_already_exists",
  "message": "A user with this email already exists.",
  "status": 409,
  "fieldErrors": null,
  "correlationId": "..."
}
```

Fields:

- `code`: stable machine-readable error code.
- `message`: human-readable message safe to show to clients.
- `status`: HTTP status code.
- `fieldErrors`: optional field-to-errors dictionary.
- `correlationId`: request correlation identifier.

Error responses must not expose stack traces, database exceptions, SQL details, internal class names, or sensitive server information.

## 7. Validation Errors

HTTP transport, model-binding, request-shape failures, and automatic invalid ASP.NET model state responses return `400 Bad Request` with code `request.malformed`.

Application-level validation failures represented by `ErrorType.Validation` return `422 Unprocessable Entity`.

`fieldErrors` is optional and may be `null` until a concrete application validation flow provides structured field-level errors.

Example:

```json
{
  "code": "validation.failed",
  "message": "One or more validation errors occurred.",
  "status": 422,
  "fieldErrors": {
    "email": [
      "Email is required."
    ]
  },
  "correlationId": "..."
}
```

## 8. HTTP Status Semantics

Use these status meanings:

- `200`: successful request.
- `201`: resource created.
- `204`: successful operation with no response body.
- `400`: malformed request or invalid HTTP request structure.
- `401`: authentication required or invalid authentication.
- `403`: authenticated but insufficient permission.
- `404`: resource not found.
- `409`: business or data conflict.
- `422`: request structure is valid but validation failed.
- `500`: unexpected server error.

## 9. Correlation ID

Every HTTP request has a correlation identifier.

Preferred header:

```text
X-Correlation-ID
```

If a valid request header is provided, the API uses it. Otherwise the API uses ASP.NET Core request tracing. Responses expose the correlation identifier in the same header and error responses include it in the JSON body.

The correlation identifier is also placed in the request logging scope.

## 10. Workspace Security Rule

Any workspace-scoped resource must be authorized and filtered by the backend.

The frontend supplying a workspace identifier is never sufficient proof of access.

Future workspace-scoped requests follow this conceptual flow:

```text
Authenticated user
    ->
Resolve requested workspace
    ->
Verify workspace access
    ->
Verify required permission
    ->
Query only data inside that workspace
```

Workspace entities, roles, memberships, policies, and final route shapes are intentionally out of scope for this step.

## 11. DTO Boundary Rule

Domain entities and EF entities must never be returned directly from API controllers.

Controllers must use explicit request DTOs and response DTOs. Do not leak persistence models, domain entities, `HttpContext`, `IActionResult`, `ProblemDetails`, ASP.NET attributes, or HTTP status codes into Domain or Application.

## 12. Mutation Contract Checklist

Every future mutation must define:

- Request DTO.
- Response DTO.
- Validation.
- Authorization.
- Conflict behavior.
- Error behavior.

This checklist must be completed before implementing Admin or Compliance mutations.
