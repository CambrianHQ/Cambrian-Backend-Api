# Programmatic licensing API retired

The previous API-key licensing integration contract is not part of the live
backend and must not be used.

API keys support read-only discovery routes only. They cannot initiate billing,
mint interactive sessions, access downloads, or mutate a user account.

Use the Cambrian web application for authenticated purchases and downloads.
Integrations may use the public V1 catalogue to discover tracks, then direct the
user to the corresponding Cambrian track page.

Live API: `https://cambrian-backend-api.onrender.com`
