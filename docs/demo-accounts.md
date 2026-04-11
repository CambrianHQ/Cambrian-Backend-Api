# Demo Accounts

These accounts are seeded by the local backend startup flow when demo users and staging data are enabled.

Default local startup:

```bash
npm run start:backend
```

Default shared password:

```text
Cambrian!Dev12345
```

Creator accounts:

- `aiden@cambrianmusic.com`
- `bellanova@cambrianmusic.com`
- `cassius@cambrianmusic.com`
- `dahlia@cambrianmusic.com`
- `ezra@cambrianmusic.com`
- `faye@cambrianmusic.com`
- `griffin@cambrianmusic.com`
- `harper@cambrianmusic.com`
- `indigo@cambrianmusic.com`
- `juniper@cambrianmusic.com`

Listener and edge-case accounts seeded with staging data:

- `listener-free@cambrianmusic.com`
- `listener-paid@cambrianmusic.com`
- `listener-heavy@cambrianmusic.com`
- `creator-noprofile@cambrianmusic.com`

Notes:

- The source of truth for these users is [`src/Cambrian.Api/StartupExtensions.cs`](../src/Cambrian.Api/StartupExtensions.cs).
- Production does not seed these accounts.
- If local startup is run with `-DisableSeedData` or `-DisableDemoUsers`, some accounts may not be created for that run.
