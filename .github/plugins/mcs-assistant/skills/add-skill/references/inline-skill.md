# Inline skill schema (authoritative)

Read only for an explicit request for inline authoring, together with the common contract, after
the `add-skill` entrypoint has routed to this exception. Establish target compatibility before
writing; if it is unknown, stop and report the gap. A new skill from an idea is not by itself
an inline request: the default is `SKILL.md` plus helper-generated upload companions.
The upload importer does not emit this variant. Do not silently convert existing skills.

```yaml
mcs.metadata:
  componentName: "make-restaurant-reservation"
  description: "Guides the user through making a restaurant reservation."
kind: InlineAgentSkill
content: |
  ---
  name: make-restaurant-reservation
  description: Guides the user through making a restaurant reservation.
  ---
  <!-- bic:source=blank -->
  <skill instructions in Markdown>
```

- `componentName` is the skill's kebab-case identifier.
- `description` must explain what the skill does so the orchestrator can select it.
- `content:` is a literal block containing the full manifest, including YAML frontmatter. Its
  `name`/`description` must match `mcs.metadata`.
- Preserve `<!-- bic:source=blank -->` verbatim on edit.
- Always double-quote externally derived `componentName` and `description` as the common contract
  requires; never interpolate raw user text into YAML.
