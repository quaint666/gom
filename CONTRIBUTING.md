# How to contribute

Nobody edits `main` directly. Every change is a branch and a **pull request**, and the owner reviews it before it merges.

- One PR, one change, about 1,500 changed lines or fewer, not counting images and fonts. Say why if it is more.
- Describe the change in plain words: "Add the invite table and its tests", not "update".
- Give the PR one type label: `enhancement` (new for the people using Gom), `bug` (a fix), `ci` (build, CI, tooling) or `documentation`. A release's notes are sorted by it (`.github/release.yml`).
- CI (`tests`) must be green. Run `dotnet test` yourself first (see `README.md`).
- Never put passwords, keys, hostnames or other people's data in a file. If one slipped in, tell the owner at once; deleting it is not enough, it stays in the history.
- When something is decided (a name, a rule, a feature we are not building), add one line to `docs/decisions.md` in the same PR.

## Before you start

Gom is small on purpose: a chat for a family and friends, run by one person on their own server. Changes that keep it simple
fit best.

- **Open an issue first** for anything bigger than a fix (the "Suggest an idea" form), and agree on the approach there before
  writing code. A fix of a clear bug can go straight to a pull request.
- **Bugs and ideas** go in the forms on the Issues page; Vietnamese or English are both fine.
- **Security holes** never go in an issue: see `SECURITY.md`.
- **No real people in issues or screenshots:** no other people's messages, names, phone numbers or pictures. Blur them, or use
  a test account.
- UI text is Vietnamese first, with English beside it (`Scripts/strings/`); code, comments and docs are English.
