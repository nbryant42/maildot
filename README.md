# Mail.Net

_It's spelled "Mail.Net", but it's pronounced "maildot."_

This project is still early-stage and developer-oriented, but it has grown beyond a pure proof-of-concept. Core mail
archiving, browsing, search, labeling, and modern HTML rendering now work well enough to support real day-to-day use
for the author.

## Project priorities

- **Robust, archive-quality data persistence:** We will default to PostgreSQL (rather than SQLite) to isolate our
  persistence engine from process crashes.
- **No forced cloud storage:** This project will never force the user to store their emails "in the cloud."
  (PostgreSQL could be cloud-hosted if the user chooses, but we default to `localhost`.)
- **Testbed for local AI inference:** I'm building out vector embeddings as a search and categorization mechanism.
- **AI tooling connectivity**: via Model Context Protocol (MCP) to connect to local or cloud-hosted AI models.

## Non-goals

- **LLM-in-the-loop email management:** Out of scope for now. Other products like LM Studio have done great work
  building LLM UX. Connect LM Studio, Claude Desktop, or similar to our MCP server for tooling.

## Project status

**Current state:**

- Core IMAP mailbox UX is implemented: folder browsing, message list/detail view, read/unread handling, delete-to-folder,
  and pagination over archived mail.
- Rich-text email rendering is substantially improved. HTML is still sanitized defensively, but common newsletter/layout
  content now renders much more faithfully, including local `cid:` inline images while keeping remote-content blocking.
- PostgreSQL persistence is a major part of the design rather than an experiment: headers, bodies, attachments, labels,
  and embeddings are stored locally, with a hybrid IMAP/Postgres source-of-truth model for long-term archival use.
- Attachments are persisted in Postgres and can now be exported back to disk from the UI. Inline signature images are
  suppressed from the attachment pane when appropriate.
- Search works in multiple modes: semantic search via vector embeddings, plus sender/subject filtering over archived mail.
- Labeling/categorization is implemented, including manual labels, sender-based labels, label unread counts, and label
  suggestions driven by embedding centroids plus a Bayesian prior lift from server-side folder placement.
- Basic MCP functionality is implemented and useful, though still incomplete relative to the full local archive model.
- Email sending is still limited: Reply/Forward currently delegate to the system default mailer via `mailto:` links,
  which means length limits and no attachment support.
- Installer/packaging remains immature. Visual Studio publish profiles for unpackaged, framework-dependent deployment
  exist, but they are not the main focus and are lightly tested.
- LLM-in-the-loop email management is still only exposed via MCP; there is no built-in autonomous assistant workflow in
  the app itself yet.

## Who is this for?

At this time? Developers, mainly. If you're looking for a production-ready email client, this is not it. If you're
looking for a testbed to experiment with AI inference over your email archive, this might be interesting.

I've made some unusual design decisions that fit my personal workflow; I still use a legacy (non-Gmail) ISP email
provider, mostly via Mozilla Thunderbird with POP3 with the "remove from server after 90 days" option enabled; the idea
is to stay within the server's size limits but archive everything locally, indefinitely, automatically. This has
some serious drawbacks. Thunderbird was a great product until about 2 years ago, but has been very unreliable lately,
with data corruption problems, search bugs, and freezes.

I can also connect to my email provider via IMAP, but baseline IMAP does not have Gmail-like labels. So this project
implements a Gmail-like multi-label system on top of IMAP, with local storage of all email data in PostgreSQL, and
labels stored locally. I can continue to let Thunderbird delete old emails from the server, while also keeping a full
archive in Postgres via this application—and gradually move away from Thunderbird.

For the above reasons, this project is designed around a somewhat POP3-like usage model where the local storage persists
even if the server data is deleted, contrary to most IMAP clients, which treat the server as the source of truth and are
designed to mirror server state locally. It's not ready to fully replace Thunderbird, but it can fill in some gaps in my
workflow.

For implementation details on the hybrid IMAP/Postgres consistency model, see [docs/source-of-truth-model.md](docs/source-of-truth-model.md).

If your IMAP provider is Gmail, you will likely be frustrated that our "labels" are not synced to Gmail labels, which
are emulated as folders when viewed via IMAP. But if your workflow is like mine, you might be in the right place.

	
## System requirements

- Windows 11 Version 22H2, x64 (Build 22621 or higher)
- DirectX 12 capable GPU (for local AI inference)
- .NET 10 Runtime
- Windows App SDK Runtime 1.8.x
- Vector embeddings use the [Qwen3-Embedding-0.6B-ONNX](https://huggingface.co/onnx-community/Qwen3-Embedding-0.6B-ONNX)
  model. The integration was developed and tested on an NVIDIA GeForce RTX 4070. It's currently tuned for 12 GB VRAM and
  32GB of system RAM, and processing a large batch of emails will most likely fail with an OOM on smaller GPUs. Tuning
  for lower VRAM GPUs is possible, but will require VRAM size detection to drive adjustments to the `MaxTokensPerBatch`
  constant in `QwenEmbedder.cs`. Let me know what values work for your GPU if you try this!

## Local Development

### Setup

1. Install Visual Studio 2026 or later with the ".NET desktop development" workload (may still work on VS 2022, but no
   longer tested).
2. Install PostgreSQL 18 or later, and create a login role and database for Mail.Net to use. It will need the
   appropriate GRANTs to create tables in its database.
3. Install the [pgvector extension](https://github.com/pgvector/pgvector) for PostgreSQL.
4. Issue `CREATE EXTENSION vector;` in your Mail.Net database (this requires superuser privileges, which you may not
   want to grant to the app role).
5. Open the solution in Visual Studio and select `Debug > Start Without Debugging` to launch the app.
6. On first run, the app will prompt for PostgreSQL connection details and store them in the Windows Credential
   Vault, and create the necessary tables if they do not already exist.
7. On subsequent runs, the app will run any Entity Framework Core migrations as needed to update the database schema.
8. The app will archive all IMAP emails in the database and compute vector embeddings for semantic search. Right now,
   there is not any feedback on the embedding process, but this can be monitored via
   [SQL queries](https://dbeaver.io/download/), such as:
   ```sql
   SELECT COUNT(*) FROM message_bodies;
   ```
   ```sql
   SELECT COUNT(*) FROM message_embeddings;
   ```



### Tools
- **TokenStats**: Console utility to compute tokenizer length stats over archived messages in Postgres.
  - Run: `dotnet run --project tools/TokenStats/TokenStats.csproj -p:Platform=x64`
  - If no tokenizer path is provided, it auto-downloads `onnx-community/Qwen3-Embedding-0.6B-ONNX/tokenizer.json` into
	`%LocalAppData%\maildot\hf\`.
  - Requires PostgreSQL credentials in the vault; reads all `message_bodies`, tokenizes subject+body, and prints
	  min/mean/median/max/stddev token counts.
- **ImapBackfill**: Console utility to re-download bodies and/or attachments from the IMAP server when local copies look
  incomplete.
  - Run: `dotnet run --project tools/ImapBackfill/ImapBackfill.csproj -p:Platform=x64 [--bodies] [--attachments] [--envelope] [--id <imapUid>]`
  - Flags are optional; omitting `--bodies`/`--attachments` refreshes both. `--envelope` skips body/attachment downloads
    and only refreshes envelope metadata; `ReceivedUtc` is derived from the first `Received:` header when bodies are
    downloaded, falling back to IMAP INTERNALDATE when headers are unavailable. Use `--bodies` when you need the
    `Received:` timestamps corrected. `--id` (or `--uid`) limits processing to a single IMAP UID. Requires IMAP +
    PostgreSQL credentials to be set in the Windows credential vault.
- **SuggestionEval**: Console utility for apples-to-apples suggestion scoring accuracy on recent labeled data.
  - Run: `dotnet run --project tools/SuggestionEval/SuggestionEval.csproj -p:Platform=x64 [--account-id <id>] [--days <n>] [--limit <n>] [--alpha <n>] [--lambda <n>] [--topk <n>] [--include-multilabel] [--sweep-lambda <v1,v2,...>]`
  - Defaults: `--days 30`, `--limit 2000`, `--alpha 24`, `--lambda 0.35`, `--topk 3`, single-label messages only.
  - Reports top-1/top-k accuracy, macro-F1, per-predicted-label precision, per-label precision/recall/F1/support, and top confusion pairs.
  - `--sweep-lambda` runs a compact comparison table (top-1/top-k/macro-F1) across multiple lambda values in one pass.
