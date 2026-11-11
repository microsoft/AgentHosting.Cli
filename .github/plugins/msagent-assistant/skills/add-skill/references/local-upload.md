# Local upload

Read only after selecting local upload. No helper command is needed to validate the path.

1. Ask for the full path if it was not provided.
2. Validate it exists and is a single `SKILL.md` (Markdown) or a `.zip`. If it is neither, tell the
   user what is accepted and stop.
3. Confirm the resolved absolute path. For a `SKILL.md`, its containing folder is the `--src` for
   optional import. For a `.zip`, you cannot extract it yourself: the only shell command available
   here is the `add-skill.bundle.js` script. Ask the user to extract it and give you the path to the
   extracted folder (the one holding `SKILL.md`), then use that as `--src`.

Return to the entrypoint's optional import decision. Do not read gallery instructions.
