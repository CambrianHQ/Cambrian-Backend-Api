# Secrets management (SOPS + age)

All backend secrets are stored **encrypted** in this repo and decrypted with an
[age](https://github.com/FiloSottile/age) key via [SOPS](https://github.com/getsops/sops).

## Files

| File | Committed? | Contents |
|------|-----------|----------|
| `config/secrets.enc.env` | ✅ yes (encrypted) | All backend secrets. Keys are readable; **values are encrypted** (`ENC[AES256_GCM,…]`). |
| `.sops.yaml` | ✅ yes | Encryption rule → the age **public** recipient. |
| age **private** key | ❌ NEVER | Lives outside the repo. See below. |

## The key (do not lose this)

- **Public recipient** (in `.sops.yaml`, safe to share):
  `age1jzzkqqjvrcvddywrt8k8uh4574z4ltnjd5cjxcdt3t9swcw2qadqmsxv93`
- **Private key** is stored at `~/.config/sops/age/keys.txt` and **must also be saved
  in the team password manager**. If it is lost, `config/secrets.enc.env` is
  unrecoverable.

## Local usage

```bash
# install tools (Windows): scoop install sops age
export SOPS_AGE_KEY_FILE="$HOME/.config/sops/age/keys.txt"   # Windows: C:\Users\<you>\.config\sops\age\keys.txt

sops -d config/secrets.enc.env            # print decrypted secrets
sops config/secrets.enc.env               # edit (re-encrypts on save)
sops exec-env config/secrets.enc.env 'dotnet run --project src/Cambrian.Api'   # run with secrets as env vars
```

## Deployment (Render)

The running service can keep reading secrets from **Render environment variables**
(unchanged) — this encrypted file is the source-of-truth backup. To have Render
decrypt at deploy instead:

1. Add a Render env var `SOPS_AGE_KEY` = the `AGE-SECRET-KEY-1…` private key value.
2. Ensure `sops` is available in the build, and start the app via
   `sops exec-env config/secrets.enc.env "<your start command>"`.

## Rules

- **Never** commit the age private key, `config/secrets.env` (plaintext), or any
  `*.dec` output — they are gitignored.
- To add/rotate a secret: `sops config/secrets.enc.env`, edit, save, commit.
- If a secret leaks, **rotate it at the provider** and re-encrypt here.
