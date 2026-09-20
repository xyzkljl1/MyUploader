# Third-party notices

This project links to `biliup` at commit `18c5bf086e943e07e9d88a905d2e5d407d6305bb`, released as v1.2.2.

- Project: https://github.com/biliup/biliup
- License: MIT OR Apache-2.0
- License text: https://github.com/biliup/biliup/blob/v1.2.2/LICENSE

The complete dependency graph and exact resolved revisions are recorded in `Cargo.lock`.

## Security audit note

The locked graph was checked with RustSec `cargo-audit` on 2026-09-20. `rustls` is pinned to 0.23.45 or later within its compatible release line to address RUSTSEC-2026-0285.

RUSTSEC-2023-0071 remains reported for `rsa` 0.9.10 and has no fixed release. The dependency comes from biliup's account-password login implementation, where it is used with a public key to encrypt a password. This tool exposes only QR-code login and never calls `login_by_password` or performs RSA private-key operations, so the vulnerable private-key timing path is unreachable. Do not add password login without re-evaluating this exception.
