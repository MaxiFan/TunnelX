# Privacy Hardening Review

Date: 2026-05-11

## What Was Checked

- Public maintainer identifiers: GitHub URL, PayPal email, creator name, donation wallet addresses.
- Secret-like text: passwords, tokens, API keys, private keys, seed phrases, UUIDs, private server domains, and private IPs.
- Local artifacts: build output, release output, downloaded archives, local test configs, and personal `.docx` notes.

## Findings

- Donation identifiers are intentionally public:
  - PayPal: `gallafan@gmail.com`
  - Crypto wallet addresses listed in `docs/DONATE.md`
- Two local test configs contain a real server IP/domain and UUID:
  - `singbox-xhttp-test.json`
  - `singbox-xhttp-test2.json`
- Local `.docx` command notes are personal working files and should not be published.
- `.github/copilot-instructions.md` contains local development notes and should not be published until it is rewritten as a sanitized contributor guide.
- Generated output, downloaded dependency archives, and extracted tool drops should not be committed.

## Actions Taken

- Added ignore rules for:
  - `singbox-xhttp-test*.json`
  - `*-local-test*.json`
  - `*.docx`
  - `.github/copilot-instructions.md`
  - publish/release/build/archive outputs
- Added sanitized sample config:
  - `examples/singbox-xhttp.example.json`
- Added stronger privacy warnings for public wallet addresses in `docs/DONATE.md` and `docs/PRIVACY.md`.
- Updated the publishing checklist to require a final `git status --short --ignored` review.

## Remaining Manual Decisions

- Consider replacing the PayPal email with a project-specific PayPal account before public release.
- Consider creating project-specific crypto wallets instead of using personal wallets.
- Do not publish raw logs without sanitizing VPN configs, UUIDs, server hostnames, IPs, usernames, and local Windows paths.
