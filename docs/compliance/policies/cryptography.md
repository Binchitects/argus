# Cryptography policy

> A template. Replace each [bracket], remove what does not apply to you,
> approve it and set its review date. What Arena does is in
> [Encryption](../README.md#encryption).

| | |
|---|---|
| Owner | [Information security officer] |
| Approved by | [Name, role, date] |
| Review | every year |

## In transit

1. Arena is reached only over HTTPS: TLS 1.2 or later with forward-secret
   ciphers (Traefik's settings), HSTS for [one year] once the certificate is
   one browsers trust (`HSTS_SECONDS`). The certificate is from [the company's
   CA / Let's Encrypt] and renewed before its end (Admin → Compliance warns).
2. Connections out of the stack (company sign-in, a cloud model, SMTP, the SIEM)
   use TLS and check the other end's certificate.

## At rest

1. The host's data disks are encrypted (LUKS, BitLocker or the storage's own),
   with the key in [a TPM / entered at boot by whom].
2. Arena seals people's files, sounds, pages and words with AES-256-GCM under a
   key derived from `APP_KEY`; secrets in its database are encrypted too.
3. Backups are encrypted with the key in `BACKUP_KEY_FILE`.

## Keys

| Key | Where | Who | Rotated |
|---|---|---|---|
| `APP_KEY` | the host's `deploy/.env`, mode 0600 | [IT] | never: changing it loses sessions, two-factor and saved secrets |
| `BACKUP_KEY_FILE` | apart from the backups: [where] | [two people] | [yearly, keeping the old for the backups it opened] |
| TLS private key | the host | [IT] | with the certificate |
| Disk keys | [TPM / escrow] | [IT] | [on compromise] |

Keys are made on the host (`openssl rand -base64 48`), never sent by e-mail or
chat, never in version control, and a copy of each is kept in [the safe / the
password manager's vault for break-glass] so a lost host does not lose the
data.

## Records

The key register above and each rotation. Kept [six years].
