import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const title = 'docs: sequence diagram drift detected';
const diagramPattern = /^docs\/[^/]*sequence-diagram\.md$/;

function git(root, args, encoding = 'utf8') {
  return execFileSync('git', ['--no-pager', ...args], {
    cwd: root, encoding, maxBuffer: 16 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'],
  });
}

function entries(value) {
  return value.split('\0').filter(Boolean);
}

export function discoverDiagrams(root, base) {
  return entries(git(root, ['ls-tree', '-rz', base, '--', 'docs']))
    .map(entry => {
      const separator = entry.indexOf('\t');
      return { mode: entry.slice(0, 6), file: entry.slice(separator + 1) };
    })
    .filter(({ file }) => diagramPattern.test(file))
    .map(({ mode, file }) => {
      if (!['100644', '100755'].includes(mode) || /[\r\n]/.test(file)) {
        throw new Error(`Unsupported diagram file: ${file}`);
      }
      return file;
    });
}

export function validateChanges(root, base) {
  if (git(root, ['rev-parse', 'HEAD']).trim() !== git(root, ['rev-parse', base]).trim()) {
    throw new Error('The audit must not change HEAD or create commits.');
  }
  const allowed = new Set(discoverDiagrams(root, base));
  const untracked = entries(git(root, ['ls-files', '--others', '--exclude-standard', '-z']));
  if (untracked.length) throw new Error(`Unexpected untracked files: ${untracked.join(', ')}`);

  const changes = entries(git(root, [
    'diff', '--no-ext-diff', '--no-textconv', '--no-renames', '--name-status', '-z', base, '--',
  ]));
  const modified = [];
  for (let i = 0; i < changes.length; i += 2) {
    const [status, file] = changes.slice(i, i + 2);
    if (status !== 'M' || !allowed.has(file)) {
      throw new Error(`Change outside the permitted modification scope: ${status} ${file}`);
    }
    const original = git(root, ['show', `${base}:${file}`], null);
    const current = readFileSync(path.join(root, file));
    const hasBom = buffer => buffer.subarray(0, 3).equals(Buffer.from([0xEF, 0xBB, 0xBF]));
    if (hasBom(original) !== hasBom(current)) {
      throw new Error(`UTF-8 BOM/encoding changed: ${file}`);
    }
    new TextDecoder('utf-8', { fatal: true }).decode(current);
    modified.push(file);
  }
  return modified;
}

function commentBody(root, file, base, summary) {
  const marker = `<!-- sequence-diagram-audit:${encodeURIComponent(file)} -->`;
  const summaryMarker = '=== DIAGRAM AUDIT SUMMARY ===';
  const markerIndex = summary.lastIndexOf(summaryMarker);
  const auditSummary = markerIndex < 0 ? summary : summary.slice(markerIndex + summaryMarker.length);
  const note = auditSummary.split(/\r?\n/).find(line => line.includes(file));
  const diff = git(root, [
    'diff', '--no-color', '--no-ext-diff', '--no-textconv', '--no-renames',
    '--src-prefix=a/', '--dst-prefix=b/', base, '--', file,
  ]);
  const longestFence = Math.max(3, ...Array.from(diff.matchAll(/`+/g), match => match[0].length));
  const fence = '`'.repeat(longestFence + 1);
  const body = [
    marker, `### \`${file}\``, '', `Audited commit: \`${base}\`.`, '',
    'The code is the source of truth. This diagram differs from the audited implementation.', '',
    '**What the audit found:**', '', note || '_See the proposed diff below._', '',
    '**Proposed fix** (Mermaid rendering succeeded before publication):', '',
    `${fence}diff`, diff.replace(/\n$/, ''), fence, '',
    `Apply this change only to \`${file}\`; do not modify source code. Preserve its existing encoding.`,
    'This is an automated proposal; verify it against the audited code before applying it.',
  ].join('\n');
  if (body.length > 65000) throw new Error(`Proposed comment is too large for GitHub: ${file}`);
  return { file, marker, body };
}

export function publishChanges({ root, base, repo, summary = '', gh }) {
  const files = validateChanges(root, base);
  const result = { changed: files.length, created: 0, updated: 0, unchanged: 0 };
  if (!files.length) return result;
  const proposals = files.map(file => commentBody(root, file, base, summary));
  const issues = gh([
    'issue', 'list', '--repo', repo, '--state', 'open', '--search', `"${title}" in:title`,
    '--limit', '100', '--json', 'number,title',
  ]);
  let issue = issues.find(item => item.title === title);
  if (!issue) {
    issue = gh(['api', `repos/${repo}/issues`, '--method', 'POST', '--input', '-'], {
      title,
      labels: ['documentation'],
      body: [
        'The sequence-diagram audit found documentation drift. Each workflow-owned comment',
        'contains the latest proposed diff for a diagram and the commit it was audited against.',
        'Later runs refresh those comments rather than posting duplicate patches.', '',
        'A maintainer may apply the fixes or, where available, assign the issue to Copilot.',
        'Review each proposal against its cited commit. Only the named diagrams should change.', '',
        `Workflow: https://github.com/${repo}/blob/${base}/.github/workflows/verify-sequence-diagrams.yml`,
      ].join('\n'),
    });
  }
  const comments = gh([
    'api', `repos/${repo}/issues/${issue.number}/comments`, '--paginate', '--slurp',
  ]).flat();
  for (const proposal of proposals) {
    const existing = comments.find(comment => comment.user.login === 'github-actions[bot]' && (
      comment.body.startsWith(proposal.marker) ||
      comment.body.split(/\r?\n/, 1)[0] === `### \`${proposal.file}\``
    ));
    if (existing?.body === proposal.body) {
      result.unchanged++;
    } else if (existing) {
      gh(['api', `repos/${repo}/issues/comments/${existing.id}`, '--method', 'PATCH', '--input', '-'],
        { body: proposal.body });
      result.updated++;
    } else {
      gh(['api', `repos/${repo}/issues/${issue.number}/comments`, '--method', 'POST', '--input', '-'],
        { body: proposal.body });
      result.created++;
    }
  }
  return result;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  const [command, base] = process.argv.slice(2);
  if (!base) throw new Error('An audited base commit is required.');
  const root = process.cwd();
  if (command === 'discover') {
    console.log(JSON.stringify(discoverDiagrams(root, base)));
  } else if (command === 'validate') {
    console.log(JSON.stringify(validateChanges(root, base)));
  } else if (command === 'publish') {
    const repo = process.env.GITHUB_REPOSITORY;
    if (!repo || !process.env.GH_TOKEN) throw new Error('GITHUB_REPOSITORY and GH_TOKEN are required.');
    const summary = readFileSync(process.argv[4], 'utf8');
    const gh = (args, input) => JSON.parse(execFileSync('gh', args, {
      cwd: root, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024,
      input: input === undefined ? undefined : JSON.stringify(input),
      stdio: ['pipe', 'pipe', 'pipe'],
    }));
    console.log(JSON.stringify(publishChanges({ root, base, repo, summary, gh })));
  } else {
    throw new Error(`Unknown command: ${command}`);
  }
}
