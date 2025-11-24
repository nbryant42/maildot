# Mail.Net

_It's spelled "Mail.Net", but it's pronounced "maildot."_

This project should be regarded as proof-of-concept level. There should be few bugs, but also few features.

## Project priorities

- **Robust, archive-quality data persistence:** We will default to PostgreSQL (rather than SQLite) to isolate our
persistence engine from process crashes.
- **No forced cloud storage:** This project will never force the user to store their emails "in the cloud."
(PostgreSQL could be cloud-hosted if the user chooses, but we default to `localhost`.)
- **Testbed for local AI inference:** I'm building out vector embeddings as a categorization mechanism.

## Local Development

### Tools
- **TokenStats**: Console utility to compute tokenizer length stats over archived messages in Postgres.
  - Run: `dotnet run --project tools/TokenStats/TokenStats.csproj -p:Platform=x64`
  - If no tokenizer path is provided, it auto-downloads `onnx-community/Qwen3-Embedding-0.6B-ONNX/tokenizer.json` into `%LocalAppData%\maildot\hf\`.
  - Requires PostgreSQL credentials in the vault; reads all `message_bodies`, tokenizes subject+body, and prints min/mean/median/max/stddev token counts.
