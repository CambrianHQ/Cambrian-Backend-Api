# JWT session migration plan

Status: launch follow-up owned by frontend and backend authentication.

The current compatibility contract still returns JWTs in authentication JSON
responses. The frontend must stop persisting those JWTs in `localStorage`.

## Target state

1. Browser sessions use an HttpOnly, Secure, SameSite cookie issued by the
   backend or a same-origin BFF.
2. Authentication responses stop returning `token` fields.
3. The frontend removes `cambrian_token` and all other JWT persistence from
   `localStorage`.
4. Cookie-authenticated mutations fetch `/auth/csrf-token`, send
   `X-CSRF-TOKEN`, and include a trusted `Origin` or `Referer`.
5. Server-to-server clients use Bearer JWTs or route-limited API keys without
   browser storage.
6. A compatibility window logs legacy token-response use before the response
   field is removed.

## Required rollout

- Inventory every frontend token read/write and auth response consumer.
- Move session hydration to cookie-backed `/auth/me`.
- Add browser integration tests for login, refresh, logout, multipart upload,
  billing, settings, and profile mutation with CSRF.
- Remove token fields from register, login, refresh, `/auth/me`, and username
  mutation responses after the frontend no longer consumes them.
- Clear legacy browser storage during the migration.

## CSP follow-up

The frontend must replace inline scripts with nonce- or hash-authorized scripts,
then remove `script-src 'unsafe-inline'`. Until the JSON-LD serializer and CSP
changes are implemented and browser-tested in the frontend repository,
production remains NO-GO for CAM-PENTEST-001.
