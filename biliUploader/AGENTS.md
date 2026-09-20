# biliUploader — calling task instructions

Read this file and README.md completely before using the tool.

## Safety contract

- This tool is for other Codex tasks. Use the JSON request, dry-run plan, confirmation digest and receipt flow exactly as documented.
- Never open, print, search, copy or commit the credentials file. The executable automatically uses the tool directory's `config.json`; only pass `--config <known-path-ending-in-config.json>` for an exceptional layout.
- Keep the real credential file at `<absolute-tool-directory>/config.json`, which Git ignores. The blank `config.example.json` template is beside it.
- The blank template documents shape only and is not usable authentication. The user obtains the real file by running `bili.ps1 login` and scanning the terminal QR code in the Bilibili mobile app. The tool uses its embedded pinned biliup v1.2.2 library and saves `config.json` automatically. Never ask the user to fill token or cookie fields manually.
- Never put cookie or token values on a command line. Do not enable HTTP/Rust trace logging or print raw upstream errors.
- Do not use browser automation, computer-use, DOM manipulation or simulated input for any Bilibili operation.
- Do not perform real development uploads. Unit and integration tests use synthetic local data and synthetic response objects.
- RustSec RUSTSEC-2023-0071 is an explicit, documented exception only because the affected RSA private-key path belongs to biliup password login, while this tool exposes QR login and never calls `login_by_password`. Read `THIRD_PARTY_NOTICES.md`; never add password login without re-evaluating it.
- `set-chapters` is deliberately unsupported because pinned biliup v1.2.2 exposes no reliable native chapter-write interface. A request containing `chapters` fails before credentials or media are read. Do not translate chapters into description timestamps and call that native chapters.

## Calling flow

1. Build once with `cargo build --manifest-path biliUploader/Cargo.toml --release`. Share this checkout and its `target/release/.bili-state/` between calling tasks; never run concurrent publish operations.
2. Create a request from `examples/`. Use a fresh UUID only for a new logical operation and retain it when reconciling an uncertain result. Paths resolve relative to the request JSON.
3. For updates, run `inspect --bvid <BVID>` first. A one-part稿件 is selected automatically for `update-video`; a multi-part稿件 requires its exact CID.
4. Run the matching command with `--dry-run --plan <new.plan.json>`. Require exit 0 and `status: dry_run_ok`. Review the normalized request, local SHA-256/size, remote snapshot, target part and changes.
5. Execute the unchanged request with `--execute --plan <same-plan> --confirm <planSha256>`. Plans expire after 15 minutes. Any local file or remote snapshot change requires a new dry-run.
6. Parse the single JSON stdout document and exit code. Exit 4 means partial/uncertain remote state: inspect the receipt and Bilibili state, and never blindly retry or change requestId to bypass it.

## Operation rules

- `create` uploads one video and creates a single-part稿件. It requires title, 1-10 tags, categoryId and copyright. Reprints require source. Optional visibility is `public` or `self-only`; omission defaults to public.
- `update-video` replaces exactly one part while preserving the稿件 metadata and other parts. It does not append. For one part omit CID; for multiple parts supply CID. Optional visibility changes the whole稿件; omission preserves it.
- `update-info` changes only supplied fields and rejects a no-op. It reads all existing fields first because biliup editing submits a complete Studio object. Optional visibility is `public` or `self-only`; omission preserves it.
- Video identifiers remain distinct: BVID identifies the稿件, AID is recorded from the archive, and CID identifies a part. Never substitute one for another.
- Credential parsing and upstream errors are always sanitized. Receipts contain no credentials or raw HTTP data.
