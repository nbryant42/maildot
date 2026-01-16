# MCP Tools Plan

Draft of read-only MCP tools that surface the maildot data model and the existing search pipeline. This is scoped to definition only; implementation will use the preview C# SDK at https://github.com/modelcontextprotocol/csharp-sdk.

## Hosting Shape
- Primary: host the MCP server inside the existing `maildot.exe` process so GPU (embedding model) memory stays shared. Start a background task after `StartImapSyncAsync` succeeds in `MainWindow.xaml.cs`; keep it alive for app lifetime.
- Configure `WebApplication` with `AddMcpServer().WithHttpTransport().WithToolsFromAssembly()` and bind to `http://localhost:3001` (or nearby). Keep the port configurable if possible.
- Register a scoped Postgres context factory that reuses the existing connection helpers/credentials; all tools are read-only.
- Use cancellation tokens on DB calls; cap result sets to keep token budgets reasonable.
- Future (optional): provide a standalone/CLI host that reuses the same tool assembly, but this is not the initial priority.

## Settings UX (WinUI)
- Add an MCP section in Settings:
  - Toggle: `Enable MCP server` (default off). When off, no listener is started.
  - Text input: `Bind address` (default `127.0.0.1`), editable.
  - Number input: `Port` (default `3001`), validate 1–65535.
- Save flow:
  - On enable/save, show warning dialog: “This may expose email information to other processes on this machine, including those run by other users.” Button: “I understand the risks.”
  - If `bind address` != `127.0.0.1`, show a stronger warning about potential remote access before accepting changes.
  - Persist settings and (re)start/stop the MCP listener accordingly.

## Project Layout
- Keep tools in the main assembly (no separate `maildot.McpServer` library required). The in-proc host can discover tool classes via `[McpServerToolType]` on static types in the existing project/assembly.
- A future CLI/standalone host can reference the same assembly and call `AddMcpServer().WithHttpTransport().WithToolsFromAssembly()`; no duplication needed.

## Data Notes
- Schema: `docs/postgres-schema.md` describes `imap_accounts`, `imap_folders`, `imap_messages`, `message_bodies`, `message_attachments`, `message_embeddings`, `labels`, `message_labels`, `sender_labels`.
- Search: `ImapSyncService.SearchInternalAsync` combines subject (`ILIKE`), sender (name/address `ILIKE`), and embedding (`< # > halfvec`) results, capped at 50. `SearchMode` supports `Auto`/`Sender`/`Content`/`All`/`Subject`. Results include `MessageId` (DB PK), `ImapUid`, `FolderFullName`, subject/from/preview/received timestamps.
- Message detail: `LoadMessageBodyAsync(folderId, messageId)` loads sanitized HTML plus headers; attachments use `message_attachments` with pg_largeobject via `NpgsqlLargeObjectManager`. Existing image fetcher already base64-encodes.

## Proposed Tool Surface
- `list_accounts`
  - Purpose: Discover available IMAP accounts.
  - Input: none.
  - Output: list of `{ id, displayName, server, username, lastSyncedAt }`.
  - Notes: IDs map to `imap_accounts.id`; hides passwords/ports/SSL flags beyond metadata.

- `list_folders`
  - Purpose: Enumerate folders for an account.
  - Input: `accountId` (int), optional `prefix` filter.
  - Output: list of `{ id, fullName, displayName, uidValidity, lastUid }`.
  - Notes: `id` is `imap_folders.id`; `fullName` matches `FolderFullName` used in search results.

- `list_labels`
  - Purpose: Enumerate labels and hierarchy.
  - Input: `accountId` (int).
  - Output: list of `{ id, name, parentLabelId }`.
  - Notes: Mirrors `labels` table; useful for future label-aware queries.

- `search_messages`
  - Purpose: Reuse existing search pipeline (subject/sender/embedding) and support no-query listing.
  - Input: optional `query` (string; when empty/null, returns messages without text/embedding filtering), optional `mode` (`auto|sender|content|all|subject`), optional `sinceUtc` (ISO 8601), optional `imapUidLessThan` for pagination, optional `maxResults` (default 60, max 1000), optional `labelIds` (list, disjunctive), optional `excludeLabelIds` (list, disjunctive), optional `folderIds` (list, disjunctive), optional `includeUnlabeled` (bool; can be combined with `labelIds` to fetch “any of these labels OR no labels”).
  - Output: `{ results: [ { messageId, imapUid, folderFullName, subject, fromName, fromAddress, preview, receivedUtc, score, source, labelIds } ], count, lowestImapUid }` where `source` is `subject|sender|embedding|list`.
  - Notes: `messageId` is DB PK; `imapUid` + `folderFullName` pair can be reused to fetch bodies/attachments. Label filtering is OR across provided IDs; folder filtering is OR across folder IDs. `includeUnlabeled` toggles the `NOT EXISTS(message_labels)` branch. No-query path ignores `mode`, orders by `ImapUid DESC`, and can page via `imapUidLessThan`. Subject and sender searches now order by `ImapUid DESC`; embedding continues to order by cosine distance. `count` and `lowestImapUid` are included for paging hints.

- `get_message_body`
  - Purpose: Fetch sanitized HTML and headers for a message.
  - Input: `folderFullName` (string), `imapUid` (long).
  - Output: `{ html, headers: { from, fromAddress, to, cc, bcc } }`.
  - Notes: Uses database copy (`message_bodies`); no network IMAP fetch. Returns minimal HTML if only plaintext exists.

- `list_attachments`
  - Purpose: List attachments for a message.
  - Input: `folderFullName` (string), `imapUid` (long), optional `contentTypePrefix` (string, default `image/`), optional `includeData` (bool, default `false`), optional `maxBytes` (int, cap when returning data).
  - Output: list of `{ fileName, contentType, sizeBytes, disposition, largeObjectId?, base64Data? }`.
  - Notes: When `includeData` is true, stream from pg_largeobject and respect `maxBytes` to avoid huge payloads. For read-only scope we can omit `largeObjectId` if undesired.

- `get_schema_snapshot` (optional helper)
  - Purpose: Provide high-level schema metadata for grounding.
  - Input: none.
  - Output: JSON with table/column summaries derived from `MailDbContext` model.
  - Notes: Derived via EF Core metadata; static, read-only.

## Implementation Pointers
- Identifier strategy: prefer returning both `message_id` (DB) and `imap_uid` + `folder_full_name` so tools can chain to body/attachments without extra lookups.
- Limits: keep all lists bounded (e.g., 200 folders, 200 labels, 50 search results, 10–20 attachments) and support paging later if needed.
- Error handling: return friendly error messages for missing accounts/folders or unavailable Postgres; surface embedding model availability in `search_messages` errors.
- Auth/context: rely on existing credential vault setup for DB connections; MCP server stays local-only (HTTP on localhost) for now.

## Next Steps
1) Confirm tool shapes above (names/fields/limits).  
2) Implement tools with `[McpServerToolType]` static class(es), reusing `MailDbContext` factory and `ImapSyncService` helpers where possible.  
3) Add lightweight README for MCP server wiring and manual test instructions.

