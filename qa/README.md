# QA Assets

This directory contains manual QA, demo-data, and partner-facing API assets.

Contents:

- `postman/`: importable Postman collection and staging environment
- `bruno/`: Git-friendly Bruno request examples
- `datasets/`: synthetic creator and track seed packs for staging/demo use

Notes:

- Requests in these collections target the current backend routes in this repo.
- Auth endpoints return the standard Cambrian API envelope, so tokens are read from `data.token`.
- Some flows, especially upload, payouts, and admin actions, require accounts with the correct role and onboarding state.
