'use strict';

const yaml = require('js-yaml');

const FIELDS = new Set(['name', 'description', 'license', 'compatibility', 'metadata', 'allowed-tools']);

function parseManifest(text, directoryName) {
  const match = String(text).replace(/^\uFEFF/, '').match(/^---[ \t]*\r?\n([\s\S]*?)\r?\n---[ \t]*(?:\r?\n|$)/);
  if (!match) throw new Error('SKILL.md must start with YAML frontmatter.');
  const doc = yaml.load(match[1], { schema: yaml.JSON_SCHEMA });
  if (!doc || typeof doc !== 'object' || Array.isArray(doc)) throw new Error('Skill frontmatter must be a mapping.');
  for (const key of Object.keys(doc)) {
    if (!FIELDS.has(key)) throw new Error(`Unsupported skill field "${key}"; put host-specific hints under metadata.`);
  }
  const name = doc.name;
  if (typeof name !== 'string' || name.length < 1 || [...name].length > 64
      || name !== name.normalize('NFKC') || name !== name.toLowerCase()
      || !/^[\p{L}\p{N}]+(?:-[\p{L}\p{N}]+)*$/u.test(name)) {
    throw new Error('Skill name must be 1-64 lowercase letters/digits with single separating hyphens.');
  }
  if (directoryName !== undefined && directoryName !== name) {
    throw new Error(`Skill name "${name}" must match its directory "${directoryName}". Rename the source directory, not the manifest implicitly.`);
  }
  if (typeof doc.description !== 'string' || !doc.description.trim() || [...doc.description].length > 1024) {
    throw new Error('Skill description must be a nonempty string of at most 1024 characters.');
  }
  for (const key of ['license', 'compatibility', 'allowed-tools']) {
    if (doc[key] !== undefined && typeof doc[key] !== 'string') throw new Error(`${key} must be a string.`);
  }
  if (doc.compatibility !== undefined && (!doc.compatibility.trim() || [...doc.compatibility].length > 500)) {
    throw new Error('compatibility must be 1-500 characters.');
  }
  if (doc['allowed-tools'] !== undefined && /,/.test(doc['allowed-tools'])) {
    throw new Error('allowed-tools must be space-separated, not comma-separated.');
  }
  if (doc.metadata !== undefined && (!doc.metadata || typeof doc.metadata !== 'object' || Array.isArray(doc.metadata)
      || Object.values(doc.metadata).some((value) => typeof value !== 'string'))) {
    throw new Error('metadata must be a string-to-string mapping.');
  }
  return doc;
}

module.exports = { parseManifest };
