# Agent Notes

- Environment is Windows; default shell is PowerShell. Use `;` to chain commands (not `&&`), and prefer native
PowerShell cmdlets (`Get-ChildItem`, `Select-String`, etc.).
- Repo is WinUI/WinAppSDK targeting `net8.0-windows10.0.19041.0` and uses MSIX tooling in the main app;
TokenStats is x64 only.
- PostgreSQL is the persistence layer; EF migrations auto-run at app startup.
DB access assumes credentials are in the Windows credential vault.
- Background IMAP sync writes headers to `imap_messages` and bodies/headers to `message_bodies`;
long-running background fetch runs with a per-call semaphore (don’t hold it).
- Tokenizer/embedding downloads cache under `%LocalAppData%\maildot\hf\` using the HuggingFace helper.
- Do not add Linux/macOS-only commands or bash scripts; stay PowerShell-compatible.
- Avoid destructive git operations unless explicitly requested (no `reset --hard`, no reverting user changes).
