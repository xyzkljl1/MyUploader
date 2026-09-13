# Repository instructions

- Keep each independent tool in its own top-level directory. Nexus publishing lives in `NexusUploader/`.
- All current and future tools must use reliable APIs, official SDKs/CLIs, or other established programmatic integrations. Never implement their functionality with browser automation, DOM/form manipulation, computer-use, simulated clicks/keystrokes, or a wrapper around those approaches. When no reliable interface exists, report the operation as unsupported; do not invent private endpoints or add a browser fallback.
- Before using NexusUploader, read `NexusUploader/AGENTS.md` and `NexusUploader/README.md` in full.
- Never open, print, search, copy, or commit credential files, including any `updater.json`. Pass an explicitly known path to the tool; the tool reads credentials internally.
- Never print API keys, cookies, signed storage URLs, or raw HTTP responses/exceptions. Do not put credentials on a command line.
- Do not publish real test mods or files during development. Use the offline HTTP test harness.
- Commit and push only when the user's entire message is `p`, or the user explicitly requests that action. Commit messages must identify AI-generated changes and the actual model; identify user-authored changes as manual modifications when present.
- If the user sends `todo <text>`, append the text as a new entry to root `TODO.md`. If the entire message is `do`, return and remove its first entry.
- Handle all exceptions in temporary .NET programs so Windows does not show an unhandled exception dialog.
- Sandbox Schannel/TLS credential errors are not evidence of invalid Nexus credentials. Verify outside the sandbox before changing application logic.
