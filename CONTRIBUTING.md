# How to contribute

Nobody edits `main` directly. Every change is a branch and a **pull request**, and the owner reviews it before it merges.

- One PR, one change, about 300 changed lines or fewer. Say why if it is more.
- Describe the change in plain words: "Add the invite table and its tests", not "update".
- CI (`tests`) must be green. Run `dotnet test` yourself first (see `README.md`).
- Never put passwords, keys, hostnames or other people's data in a file. If one slipped in, tell the owner at once; deleting it is not enough, it stays in the history.
- When something is decided (a name, a rule, a feature we are not building), add one line to `docs/decisions.md` in the same PR.
