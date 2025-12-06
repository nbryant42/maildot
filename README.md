# Mail.Net

_It's spelled "Mail.Net", but it's pronounced "maildot."_

This project should be regarded as proof-of-concept level. There are few features beyond the semantic search experiment.

## Project priorities

- **Robust, archive-quality data persistence:** We will default to PostgreSQL (rather than SQLite) to isolate our
persistence engine from process crashes.
- **No forced cloud storage:** This project will never force the user to store their emails "in the cloud."
(PostgreSQL could be cloud-hosted if the user chooses, but we default to `localhost`.)
- **Testbed for local AI inference:** I'm building out vector embeddings as a search and categorization mechanism.

## Project status

**Proof-of-concept:**

- Basic email receiving UX works.
- Basic rich-text email rendering: works, but HTML is sanitized heavily. Needs improvement.
- PostgreSQL persistence works.
- Search: semantic search via vector embeddings, and a simple sender filter over archived emails.
- Email sending (Reply, Forward) is a kludge, which delegates to the system default mailer via mailto: links,
  which have length limits and no attachments.
- Categorization via vector embeddings centroids: planned but not yet implemented.
- Installer/packaging: not yet implemented. VS publish profiles for an unpackaged, framework-dependent deployment exist,
  and should work, but are mostly untested.
	
## Hardware requirements

- Windows 10 Version 2004, x64 (Build 19041 or higher)
- DirectX 12 capable GPU (for local AI inference)
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
  - Run: `dotnet run --project tools/ImapBackfill/ImapBackfill.csproj -p:Platform=x64 [--bodies] [--attachments] [--id <imapUid>]`
  - Flags are optional; omitting both refreshes bodies and attachments. `--id` (or `--uid`) limits processing to a single
    IMAP UID. Requires IMAP + PostgreSQL credentials to be set
	in the Windows credential vault.
