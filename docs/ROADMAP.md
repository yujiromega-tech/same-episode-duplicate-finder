# Duplikates Roadmap

This roadmap starts from the current WinForms production/test app. WinForms remains the active application until the core workflow code is extracted enough to support an Avalonia shell without duplicating business logic.

## Current Position: Phase 2.7 Stabilization and Shell Polish

- Keep the current WinForms shell.
- Fix visible clipping, panel sizing, stale text, and inspector usability issues.
- Keep the series rail fast and list-based; cover art loads in the selected-series header and inspector.
- Preserve duplicate review, missing episodes, episode search, selected RSS, metadata, cover lookup, monitoring, delete, and move workflows.
- Keep destructive actions review-first and explicit.

## Phase 2.7A: UI Defect Cleanup

- Fix title overflow in the selected-series header.
- Keep provider badges readable without clipping.
- Keep Candidates and Ready to Remove usable when both are visible.
- Avoid duplicated summary text under the selected series title.
- Add in-app artwork repair paths for wrong covers:
  - remove selected series artwork
  - retry selected series artwork fetch
  - log provider match confidence
- Keep automatic artwork writes strict enough to avoid overwriting good art with a weak match.

## Phase 2.7B: Diagnostic Code Cleanup

- Remove obsolete UI branches that are no longer product behavior.
- Condense repeated status and settings reads where safe.
- Audit visible strings for tester-facing roadmap language.
- Identify large repeated form patterns for later extraction rather than risky inline rewrites.
- Keep the build and tests green after each cleanup pass.

## Phase 2.8: Duplicate Workflow Refinement

- Improve duplicate table readability and grouping.
- Keep deletion safety visible and explicit.
- Improve selected-file inspector behavior.
- Add clearer review states for high confidence, medium confidence, low confidence, no cover, and manual review.
- Keep recommendations heuristic and user-reviewed.

## Phase 2.9: Action Plan Preview

- Expand `LibraryActionPlanner` into a user-visible preview of suggested next steps.
- Keep the planner non-destructive.
- Show unified work items such as:
  - fetch missing cover
  - review duplicate delete candidate
  - search missing episode
  - add reliable full-season release to selected RSS
  - manual review needed
- Use existing scan, recommendation, cover, missing episode, search, and RSS data.

## Phase 3.0: Core Workflow Extraction

- Move scan, duplicate scoring, missing episode analysis, cover/provider planning, search planning, selected RSS planning, and file action planning out of the WinForms form where practical.
- Keep WinForms as the first consumer of the extracted core.
- Add focused tests around each extracted workflow.
- Keep provider calls throttled and capped.
- Keep storage, cache, log, and report paths documented before any migration.

## Phase 3.1: Horn-Clause Planning Layer

- Represent workflow state as facts.
- Derive safe suggested actions from simple rules.
- Start with direct Horn-clause-style rule evaluation rather than a full Rete network.
- Example rule shape:

```text
IF duplicate_candidate
AND high_confidence
AND keep_file_has_cover
THEN suggest_delete_review
```

- Keep all destructive actions review-only.
- Log which rule produced each suggestion.

## Phase 3.2: Rete Evaluation Spike

- Evaluate whether a Rete-style matcher is useful after the simpler rule planner exists.
- Use Rete only if there are enough changing facts and rules to justify it, such as scan updates, provider status changes, RSS updates, monitoring results, and user selection changes.
- Keep this as a spike first.
- Do not replace working planner behavior unless tests show a clear benefit.

## Phase 3.3: Shared Rules Service

- Wrap the planner or Rete-backed matcher behind a small shared service interface.
- Keep rules testable outside the UI.
- Keep WinForms connected first.
- Make the service reusable by a future Avalonia shell.

## Phase 4.0: Avalonia Prototype

- Start a new Avalonia shell after core workflows and planning services are reusable.
- Treat Avalonia as the multi-platform UI path.
- Keep WinForms available as the stable Windows application during the transition.
- Avoid changing provider, delete, move, RSS, or scan logic only for UI reasons.

## Phase 4.5: Multi-Platform Hardening

- Validate Windows, Linux, and macOS file-path behavior.
- Rework OS-specific file actions behind platform services.
- Add platform-specific handling for:
  - recycle/trash behavior
  - opening file locations
  - launching magnet links
  - local RSS feed hosting
  - secure settings storage

## Phase 5.0: Release Readiness

- Add signed release builds where possible.
- Add screenshots and usage examples.
- Add broader workflow tests.
- Add a license before public release.
- Keep generated local data, logs, caches, API keys, and reports out of Git.
