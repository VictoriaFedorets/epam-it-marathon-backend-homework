# Frontend API handoff: Level 3 and Remove Guest

This guide describes the current backend implementation for the customer group-reservation flow. It was checked against `RouteRegistry`, handlers, models, services, `deployment_resources.json`, and the OpenAPI file. It documents the current wire behavior, including casing differences from parts of the OpenAPI spec.

## Base URL and authorization

The OpenAPI `develop` server is:

```text
https://a7quy3t3fj.execute-api.eu-north-1.amazonaws.com/develop
```

Keep the base URL configurable by frontend environment. The three endpoints listed in [AWS develop availability](#aws-develop-availability) are not currently published to that live stage.

There are two different bearer-token types:

| API group | Header | Purpose |
|---|---|---|
| Cognito-protected customer/owner APIs | `Authorization: Bearer <idToken>` | Customer identity; use the `idToken` returned by `/signin`. |
| Guest-session APIs | `Authorization: Bearer <guestSessionToken>` | Access to one specific guest session's cart. This is an opaque token, not a Cognito token. |
| Public APIs | No `Authorization` header | Sign-in, invite lookup, guest-session creation, and group status. |

Use the exact `Bearer ` prefix. Never log or expose either token. Guest-session credentials are not interchangeable between sessions.

Most errors have one of these JSON shapes:

```json
{ "error": "Readable error message" }
```

```json
{ "message": "Readable error message", "errorCode": "GroupReservationExpired" }
```

The second format is used for validation exceptions on guest-session APIs. Possible `errorCode` values are `GroupReservationExpired`, `GroupReservationSubmitted`, `GuestLimitExceeded`, `GuestSessionExpired`, and `Unknown`. A few endpoints have legacy response-shape differences noted below; do not assume every error is `{ "error": ... }`.

## Endpoint index

| Method and URL | Authorization | Use |
|---|---|---|
| `POST /signin` | Public | Customer sign-in |
| `POST /reservations` | Cognito | Create reservation; `guestCount >= 2` creates a group |
| `GET /reservations/{reservationId}/invite-token` | Cognito; owner only | Get invite token to build the frontend join URL |
| `GET /guest/invites/{token}` | Public | Resolve invite and obtain `groupReservationId` |
| `GET /reservations/{reservationId}/guest-session-id` | Cognito; owner only | Get Owner session ID and guest-session bearer token |
| `GET /guest/groups/{groupReservationId}/status` | Public | Read group status and participant counts |
| `GET /guest/groups/{groupReservationId}/sessions` | Cognito; owner only | Read participant carts and totals |
| `POST /guest/sessions` | Public | Create Guest session |
| `GET /guest/sessions/{guestSessionId}` | Guest-session bearer | Read one cart |
| `GET /guest/sessions/{guestSessionId}/menu` | Guest-session bearer | Read menu for the reservation location |
| `POST /guest/sessions/{guestSessionId}/dishes` | Guest-session bearer | Add quantity of a dish |
| `DELETE /guest/sessions/{guestSessionId}/dishes` | Guest-session bearer | Remove quantity of a dish |
| `POST /guest/sessions/{guestSessionId}/submit` | Guest-session bearer | Confirm one participant's cart |
| `POST /group-reservations/{groupReservationId}/submit` | Cognito; owner only | Submit the consolidated group pre-order |
| `DELETE /guest/groups/{groupReservationId}/sessions/{guestSessionId}` | Cognito; owner only | Remove a Guest and receive the refreshed snapshot |

All path IDs and tokens must be URL-encoded when inserted into URLs.

## Contracts

### 1. Sign in

`POST /signin` — public, `Content-Type: application/json`.

Request:

```json
{ "email": "customer@example.com", "password": "..." }
```

Success `200`:

```json
{
  "message": "Sign in successful",
  "idToken": "<cognito-id-token>",
  "accessToken": "<cognito-access-token>"
}
```

Use `idToken` as the Cognito bearer token for the owner APIs below. Errors: `400` for empty/invalid input or validation; `401` for invalid credentials. Error body: `{ "error": "..." }`.

### 2. Create a multi-guest reservation

`POST /reservations` — Cognito bearer (`idToken`).

Request fields:

```json
{
  "tableId": "table-id",
  "startTime": "2026-10-30T18:00:00Z",
  "endTime": "2026-10-30T20:00:00Z",
  "guestCount": 4
}
```

`guestCount` includes the Owner. A group reservation and Owner guest session are created when `guestCount >= 2`. For a customer-created reservation, the authenticated customer is used as the owner; do not rely on a client-supplied `customerId`. `customerEmail` is intended for waiter-created reservations.

Success `201` returns a reservation object with fields `id`, `tableId`, `startTime`, `endTime`, `guestCount`, `status`, `customerId`, `waiterId`, `locationAddress`, `restaurantName`, `hasSubmittedPreOrder`, and `guestInviteLinkAvailable`. The response does **not** contain `groupReservationId`.

Errors: `400` invalid body/reservation details; `401` missing/invalid authenticated identity; `403` forbidden reservation action. Error body: `{ "error": "..." }`.

### 3. Get an invite token and resolve the invite

#### Owner retrieves token

`GET /reservations/{reservationId}/invite-token` — Cognito bearer; the caller must own the reservation.

Success `200`:

```json
{ "token": "<invite-token>" }
```

The API returns a token, not a complete URL. Build the frontend link using the frontend origin and its join route, for example:

```text
https://<frontend-origin>/join/<token>
```

Errors: `400` missing reservation ID/validation; `401` unauthenticated; `403` caller is not the owner; `404` reservation/group not found; `409` group submitted or closed. Standard error bodies are `{ "error": "..." }`, except coded validation can use `{ "message": "...", "errorCode": "..." }`.

#### Guest (or Owner) resolves invite

`GET /guest/invites/{token}` — public.

Success `200` contains the group and reservation information needed by the join UI:

```json
{
  "GroupReservationId": "group-id",
  "LocationId": "location-id",
  "RestaurantName": "Green & Tasty",
  "LocationAddress": "address",
  "StartTime": "2026-10-30T18:00:00Z",
  "EndTime": "2026-10-30T20:00:00Z",
  "MaxGuestCount": 4,
  "TableNumber": 1,
  "InviterName": "Owner"
}
```

**Wire casing:** the current handler serializes this DTO with the default serializer, so the property names are PascalCase as shown. The OpenAPI schema lists camelCase names; treat the runtime response as authoritative unless the backend later normalizes it.

Errors: `400` missing/invalid token, closed group, or group at capacity; `404` token/group not found. Capacity/closed validation may return the coded-error shape. Resolve and retain `GroupReservationId` early: the create-reservation response does not return it, and invite lookup stops succeeding once the group reaches capacity or closes.

### 4. Owner session credentials and cart

#### Get the Owner session ID and token

`GET /reservations/{reservationId}/guest-session-id` — Cognito bearer; owner only.

Success `200`:

```json
{
  "guestSessionId": "owner-session-id",
  "guestSessionToken": "<opaque-base64-token>"
}
```

The backend generates 32 random bytes, returns the base64 token once, and stores its SHA-256 hash. **Every call rotates the token and invalidates the previously issued token for this Owner session.** Fetch it once per active Owner session and retain the latest value; avoid refetching during normal renders or in parallel tabs.

The response does not contain `groupReservationId`; obtain that from the invite lookup above and cache it alongside the reservation ID.

Errors: `400` missing reservation ID; `401` unauthenticated; `403` caller is not the reservation owner; `404` reservation, group, or Owner session not found. Body: `{ "error": "..." }`.

Use `guestSessionId` and `guestSessionToken` with the guest-session cart endpoints below, including GET, menu, add/remove dishes, and per-session submit. This is how the Owner uses their own cart; no separate Owner cart endpoint is needed.

#### Read one cart

`GET /guest/sessions/{guestSessionId}` — guest-session bearer token.

Success `200`:

```json
{
  "name": "Owner or guest name",
  "items": [
    { "dish": { "id": "dish-id", "name": "Dish", "price": 12.5 }, "quantity": 2 }
  ],
  "total": 25.0,
  "isSubmitted": false
}
```

The nested `dish` is a `DishResponse` with fields including `id`, `name`, `description`, `dishType`, `price`, nutritional fields, `imageUrl`, and `state`.

Errors: `401` missing/invalid guest token; `404` guest session not found. **Current handler returns the 404 message as a JSON string** (for example `"Guest session was not found."`), not as `{ "error": "..." }`.

#### Read the menu

`GET /guest/sessions/{guestSessionId}/menu` — guest-session bearer token.

Success `200` returns `{ "dishes": { "<DishType>": [ ... ] } }`. Menu entries include `id`, `name`, `description`, `price`, `weight`, nutrition fields, `imageUrl`, `state`, and `selectable`. Only available dishes can be added.

Errors: `400` validation/group state; `401` invalid guest token; `403` forbidden; `404` group/reservation not found. Validation errors may use the coded-error shape.

#### Add/remove dishes

`POST /guest/sessions/{guestSessionId}/dishes` adds; `DELETE /guest/sessions/{guestSessionId}/dishes` removes.

Both require the guest-session bearer token and this JSON body:

```json
{ "dishId": "dish-id", "quantity": 2 }
```

Quantity must be a positive integer. Add accumulates quantity for a dish already in the cart. Remove subtracts quantity and removes the cart item if its remaining quantity is zero or less.

Success `200`:

```json
{ "message": "Dish added successfully." }
```

The remove operation returns `"Dish removed successfully."`.

Errors: `400` invalid body, non-positive quantity, unavailable/nonexistent dish, or dish not in cart; `401` invalid guest token; `409` group expired/submitted; `500` unexpected server error. Some validation errors use `{ "message": "...", "errorCode": "Unknown" }`; other errors use `{ "error": "..." }`.

#### Confirm one participant's pre-order

`POST /guest/sessions/{guestSessionId}/submit` — guest-session bearer token; no request body.

This confirms **one session's cart**, not the entire group. The cart must contain dishes.

Success `200`:

```json
{ "message": "Cart submitted successfully." }
```

Errors: `401` invalid guest token; `404` missing guest session ID; `400` cart/validation error; `409` cart already submitted or group expired/submitted. The cart cannot be edited after its session is submitted.

### 5. Group snapshot and status

#### Get all participant carts (Owner screen)

`GET /guest/groups/{groupReservationId}/sessions` — Cognito bearer; owner only.

Success `200`:

```json
{
  "participants": [
    {
      "id": "session-id",
      "type": "Owner",
      "name": "Owner",
      "items": [
        { "dish": { "id": "dish-id", "name": "Dish", "price": 12.5 }, "quantity": 2 }
      ],
      "total": 25.0,
      "isSubmitted": true
    }
  ],
  "total": 25.0
}
```

`type` is `Owner` or `Guest`. Totals are calculated by the backend. The response includes all current sessions and their `isSubmitted` state.

Errors: `400` missing ID/validation; `401` unauthenticated; `403` caller is not the owner; `404` group or parent reservation not found. Validation errors may use the coded-error shape.

#### Get group status

`GET /guest/groups/{groupReservationId}/status` — public.

Success `200`:

```json
{ "currentParticipants": 3, "maxCapacity": 4, "status": "Open" }
```

`currentParticipants` includes the Owner. `status` is `Open`, `Submitted`, or `Closed`. Errors: `400` missing ID/validation; `404` group not found; `500` unexpected server error. Standard error body: `{ "error": "..." }`.

Use the Owner sessions endpoint for carts/totals and this endpoint when the UI needs authoritative group status/capacity. Stop group polling once status is no longer `Open`, after group Submit, and when leaving the Owner screen.

### 6. Submit the whole group (Owner)

`POST /group-reservations/{groupReservationId}/submit` — Cognito bearer; owner only.

Request:

```json
{ "reservationId": "reservation-id" }
```

The `reservationId` must be the reservation associated with the path's group. All **submitted** session carts, including the Owner's, are consolidated by `dishId`; quantities for the same dish are summed.

Success `200` returns the consolidated items:

```json
[
  { "dishId": "dish-id", "quantity": 3 }
]
```

The group is then marked `Submitted`. The operation succeeds only once.

Errors: `400` invalid/mismatched reservation, no submitted sessions, submitted carts contain no dishes, or no order exists; `401` unauthenticated; `403` caller is not the reservation owner; `404` group/reservation not found; `409` group is closed or already submitted. Current handler uses `{ "error": "..." }` for these errors.

### 7. Remove Guest (Owner)

`DELETE /guest/groups/{groupReservationId}/sessions/{guestSessionId}` — Cognito bearer; owner only. No request body.

The service deletes only a non-owner session belonging to this group, then returns the refreshed group snapshot (`participants` and overall `total`) in the same shape as `GET /guest/groups/{groupReservationId}/sessions`. Update the UI directly from this response; do not optimistically recalculate prices.

Errors:

| Status | Meaning |
|---|---|
| `400` | Missing IDs, target is the Owner session, or target belongs to another group. Validation body may be `{ "message": "...", "errorCode": "Unknown" }`. |
| `401` | Missing authenticated identity. |
| `403` | Caller is not the reservation owner. |
| `404` | Group, reservation, or session not found. |
| `409` | Group is `Submitted` or `Closed`. |

An individual Guest's cart being `isSubmitted: true` is **not** the same as the whole group's status becoming `Submitted`. Current backend code permits the Owner to remove that Guest while the group is still `Open`; it blocks deletion after group Submit/closure and always blocks deleting the Owner session. Confirm with product whether the UI should additionally hide removal for an individually submitted cart.

## Recommended end-to-end flows

### Owner

1. `POST /signin`; keep the returned `idToken`.
2. `POST /reservations` with `guestCount >= 2`; retain `reservation.id`.
3. Immediately call `GET /reservations/{reservationId}/invite-token`, then `GET /guest/invites/{token}` while the group has capacity. Retain `GroupReservationId` and expose the frontend link `/join/{token}`.
4. Call `GET /reservations/{reservationId}/guest-session-id`; retain both `guestSessionId` and the latest `guestSessionToken`. The endpoint rotates the token on each call.
5. Use `Authorization: Bearer <guestSessionToken>` for `GET /guest/sessions/{guestSessionId}/menu`, then add dishes with `POST .../dishes`. Optionally read the cart with `GET /guest/sessions/{guestSessionId}`.
6. Confirm the Owner's pre-order with `POST /guest/sessions/{guestSessionId}/submit`. This is the per-session confirmation.
7. Fetch the Owner snapshot with `GET /guest/groups/{groupReservationId}/sessions`. For refresh/polling while `status` is `Open`, use this snapshot and `GET /guest/groups/{groupReservationId}/status`.
8. Submit the whole group once with `POST /group-reservations/{groupReservationId}/submit` and `{ "reservationId": "<reservationId>" }`. Stop polling; handle `409` as already submitted/closed.

### Guest

1. Open frontend route `/join/{token}`.
2. Call public `GET /guest/invites/{token}`. If it succeeds, show reservation details and retain `GroupReservationId`; handle `400` as expired/full/invalid and `404` as unknown invite.
3. Collect the guest name and call `POST /guest/sessions`:

   ```json
   { "groupReservationId": "group-id", "guestName": "Guest name" }
   ```

4. **Read the actual response casing:** current response is `{"SessionId":"...","SessionToken":"..."}`. Save these as guest session ID/token.
5. Use `Authorization: Bearer <SessionToken>` to load menu/cart and add/remove dishes.
6. Confirm the guest cart with `POST /guest/sessions/{SessionId}/submit`.

### Owner removes a Guest

1. Show a confirmation modal before calling DELETE.
2. Call `DELETE /guest/groups/{groupReservationId}/sessions/{guestSessionId}` with the Owner's Cognito `idToken`.
3. On `200`, replace participant data and totals with the returned snapshot. On `409`, reload group status/snapshot and communicate that the group is no longer editable.

## AWS develop availability

The local `RouteRegistry` and `deployment_resources.json` contain all the Level 3 routes, but the previously inspected live AWS `develop` API does **not** have these three method/path integrations:

| Missing from live `develop` | Present in local backend/config |
|---|---|
| `GET /reservations/{reservationId}/guest-session-id` | Yes |
| `GET /guest/groups/{groupReservationId}/sessions` | Yes |
| `POST /group-reservations/{groupReservationId}/submit` | Yes |

They will return a gateway-level not-found response in the current live stage until separately deployed and verified. Do not treat their presence in the local deployment file or Swagger as proof that they are already available in AWS. No deployment was performed for this handoff.

## Contract notes for frontend integration

1. `POST /reservations` does not return `groupReservationId`; resolve it from the invite endpoint early and retain it.
2. Owner-session token retrieval is a credential rotation, not an idempotent read. Re-fetching invalidates the prior Owner guest token.
3. `POST /guest/sessions` and `GET /guest/invites/{token}` currently serialize DTO property names in PascalCase; the invite lookup response also differs from the camelCase schema in Swagger.
4. Error bodies are not uniform. Handle status code first, then parse `{error}`, `{message,errorCode}`, or the JSON string returned by guest-session GET 404.
5. Guest cart Submit and group Submit are two separate stages. The group Submit only includes participant sessions whose `isSubmitted` is true.

