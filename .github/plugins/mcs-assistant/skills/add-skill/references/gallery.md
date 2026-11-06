# Gallery acquisition

Read only after selecting the Power CAT Cat Agent Skills gallery
(`https://microsoft.github.io/cat-agent-skills/`, source `github.com/microsoft/cat-agent-skills`).
Resolve `<scriptPath>` as the skill-local `scripts/add-skill.bundle.js`.

1. List the available skills as a **table**:

   ```bash
   node "<scriptPath>" list --pretty
   ```

   Columns are `#`, `◆` (bundle marker), `Name`, `Description`, and `Slug`, sorted by display name
   and limited to Copilot Studio skills. The render width and sort are deterministic regardless of
   terminal size. The `◆` marks extra `scripts/`, `references/`, or `assets/`; an empty cell means a
   single `SKILL.md`. Use `--all` only if the user explicitly requests every platform.
   Plain `list` returns JSON `{ ok, count, skills: [{ slug, name, description, platforms, tags, hasBundle }] }`.
   Stdout is paged (20 entries by default). Read stderr totals for `--pretty`; use `--offset`
   and `--limit` to browse further pages before claiming the table is complete.
   JSON mode also accepts `--output "<new-file.json>"` for complete results; it cannot be
   combined with `--pretty`. `--help` describes all options.
2. Present the table without flattening it into a list. Ask for a number or name, then map that
   choice to the exact `slug`. If the request already named a skill, resolve and confirm the match.
3. Download the selected skill. **Do not ask the user where to save it**; omit `--dest` so the helper
   uses `<os-tmp>/mcs-add-skill/<slug>`:

   ```bash
   node "<scriptPath>" download --slug "<slug>"
   ```

   Result JSON is `{ ok, slug, name, hasBundle, dir, zip, files }`. Use the returned `dir` as `--src`
   if the user subsequently chooses import; do not prompt for a location. It contains the manifest
   and all scripts/references/assets. A prebuilt bundle also saves `<os-tmp>/mcs-add-skill/<slug>.zip`.
   `files` is paged in stdout; inspect `pages.files` or the explicit full JSON output.
   Acquisition does not establish manifest validity or name/directory equality. Import validates
   those separately; follow `import.md` rather than silently rewriting a downloaded manifest.

Return to the entrypoint's optional import decision. Do not read local-upload instructions.

## Failure

Surface helper errors. A truncated tree, rate limit, or network failure is transient: report it and
offer a retry. `GITHUB_TOKEN` raises the API limit for the tree call but is not normally required.
If download refuses a non-skill entry (Scout automation or legacy ZIP), relay the error and suggest
an actual skill.
