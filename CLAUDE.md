@AGENTS.md

## Claude Code

This file provides guidance to Claude Code (claude.ai/code) specifically; the shared repository
guidance above (architecture, commands, tests, conventions) applies to any agent working here.

This repo's Claude Code skills live in `.claude/skills/` (portable stubs are mirrored under
`.agents/skills/` for other agents — see AGENTS.md's "Agent Skills" section). Prefer invoking them
via the Skill tool over re-deriving their workflows inline:

- `implement-prd` — work through one of this repo's own staged `new-features/*.md` PRDs end to end.
- `spec-to-staged-plan` — turn an externally-drafted spec into a verified, staged PRD in this
  repo's own style, before `implement-prd` can be used on it.
- `optimize-pgfcodec` — iteratively profile and optimize the managed codec for end-to-end
  performance.
