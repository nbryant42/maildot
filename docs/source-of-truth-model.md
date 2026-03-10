# Source-of-Truth Model

Mail.Net is not a mirror-style IMAP client.

The design goal is to preserve a local archive even when messages later disappear from the IMAP server. That means
there is no single source of truth for every UI decision.

## Core rules

- The IMAP server is authoritative for the current live server state.
- PostgreSQL is authoritative for the local archive.
- A message may remain in Postgres after it no longer exists on the server.
- `ImapUid <= 0` is not a reliable test for "local only".
  A positive UID can still refer to a message that has disappeared from the server since the last sync.

## Practical implications

- Folder contents are a hybrid view.
- Label contents are local, because labels are local-only features.
- Read/unread state is also hybrid:
  live IMAP state matters for server-backed messages, but local unread rows may still be shown when we bias toward
  over-inclusion.

## UI policy

- `All` in a folder view should prefer showing too much over showing too little when local archive rows are involved.
- `Unread only` in a folder view is a convenience filter, not a strict "current IMAP unread state" guarantee.
- Current policy for `Unread only`:
  - include unread messages returned by IMAP `UNSEEN` / `NOTSEEN`
  - include local Postgres rows where `IsRead = false`
  - merge and dedupe
  - prefer over-inclusion to accidental omission

This means a message can occasionally appear in `Unread only` because the local archive still marks it unread even
though the server may now consider it read. That tradeoff is intentional.

## Why we do not persist a "local only" flag

We cannot safely persist a durable `IsLocalOnly` truth bit, because server presence can change at any time outside the
app. Any persisted flag would drift unless it were continuously revalidated against the server, which defeats the point
of treating it as ground truth.

Instead, server presence is inferred opportunistically from current IMAP queries when needed, and many UI paths simply
accept that the archive model should bias toward over-display.
