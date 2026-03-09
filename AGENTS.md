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
- In Codex VS Code plugin (`Default permissions`) environments with NuGet sandboxing, offline build works with:
  `dotnet build maildot\maildot.csproj -c Debug --no-restore /p:Restore=false /p:RestorePackages=false /p:NuGetAudit=false -v minimal`
- Do not add Linux/macOS-only commands or bash scripts; stay PowerShell-compatible.
- Avoid destructive git operations unless explicitly requested (no `reset --hard`, no reverting user changes).
- The `maildot` MCP server at http://localhost:3001 is tools-only.
- Expected tools: list_accounts, list_folders, list_labels, search_messages, get_message_body, list_attachments, get_schema_snapshot.
- when probing for MCP features here, skip resources/list and go straight to tool calls
(or a tools/list endpoint if present).
- Commit message format: short header line, body with a bit more detail,
end with `Co-authored-by: Codex (GPT-5.4) <codex@openai.com>`.
