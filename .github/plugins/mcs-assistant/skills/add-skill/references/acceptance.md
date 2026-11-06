# Skill acceptance checks

Read after a successful import. Check the actual target folder, not just the source manifest.

## Local materialization

1. Require helper exit code zero and `{ ok: true }`. Resolve `behaviorsDir` within the intended
   agent workspace and verify the reported files exist. Report every warning.
   An import dry-run is a plan, not successful materialization. Check `pages` totals and read
   complete results from the explicit output file when stdout is paged or size-truncated.
2. For the default upload layout, require exactly cased `SKILL.md` and a generated
   `skill.mcs.yml` with `kind: InlineAgentSkill`, `authoringSource: Upload`, the expected
   component name, and a valid unique schema name no longer than 100 characters.
   Parse the manifest frontmatter and anchor YAML; check the description agrees with the source.
3. A single-manifest skill must have no `bundle`, `manifestSchemaName`, or payload sidecars.
   A bundle must have the bundle/manifest metadata and one metadata-only sidecar for each
   non-manifest payload, as specified in `uploaded-skill.md`. Do not hand-repair generated files.
4. Check that the source manifest and payload were preserved and that no competing inline
   component or stale anchor was left in the same skill folder. Verify unrelated skills,
   agent settings, and CLI-managed metadata were not changed by import.
5. Apply the procedure to a representative request and a missing-input/evidence or tool-error
   case. Verify output fields, attribution, uncertainty and side-effect confirmation rules.
   Label offline instruction simulations as such, not as live agent tests.

For model behavior, use a repeatable positive/near-miss trigger corpus and normal/error outcome
fixtures. Compare fresh runs with a no-skill-body or previous-version baseline, retain prompts,
assertion results, traces and timing/token usage, and review actual artifacts. The repository's
`evals/` harness runs tool-free live model probes; these are not native activation or cloud tests.

A documented bare fallback is not the default upload-layout acceptance. Report it explicitly.
Only an extension workspace may rely on later companion synthesis; verify that synthesis before
claiming the skill is recognized. Do not accept a bare fallback for an MSAGENT-only workflow.

## Recognition and cloud acceptance

Local materialization does not prove cloud acceptance, synchronization recognition, runtime
selection or publication. Stop at local checks when that is all the user requested; report
cloud acceptance as unverified, not failed or passed.

- If the extension is available, inspect its Skills/Agent Changes view for recognition and
  validation errors. Do not claim recognition based only on the folder existing.
- For a separately requested MSAGENT upload, use the `push-agent` workflow; for explicit
  publication, use `publish-agent`. Follow their exact selection, consent, pull/conflict and
  failure checks. Never deploy merely for validation without the user's consent.
- After an authorized synchronization, inspect its result for the expected skill and file
  changes, warnings or unsupported components. Do not equate aggregate success with acceptance
  of these particular skills. A normal authorized pull/round-trip must preserve the manifest,
  generated anchor and payload representation before claiming round-trip acceptance. Preserve
  originals and stop on conflicts or unexplained differences; do not overwrite or silently
  convert variants to force a successful result.
- Only a separately authorized published-agent test can establish runtime behavior. Test
  both a normal request and insufficient evidence; do not expose private reasoning traces.

Report precisely which levels were checked: local files, extension recognition, cloud
synchronization/round-trip, publication and runtime. Never imply unperformed checks succeeded.
