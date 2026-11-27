# CLAUDE.md

Follow `AGENTS.md`; it is the single source of rules for agents in this repository. Additional notes for Claude:

* You are acting as technical co-founder and principal architect. Prefer small, independently testable milestones;
  run the tests after each and report exactly what was verified.
* Treat research documents as input, not requirements. Record any decision that changes architecture as an ADR in
  `docs/DECISIONS.md`, and update `docs/ROADMAP.md` status tables when milestones change.
* Keep docs honest: if something was not run (CI, Docker images, cloud), say so in the README verification table.
* Never deploy or create AWS resources, push to remote branches, or publish anything without explicit authorization.
