import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { discoverDiagrams, publishChanges, validateChanges } from './sequence-diagram-audit.mjs';

const diagram = 'docs/auth-sequence-diagram.md';
const initialDiagram = '# Sign-in\n\n```mermaid\nsequenceDiagram\n    User->>CLI: login\n```\n';

function repository(t, files = { [diagram]: initialDiagram }) {
  const root = mkdtempSync(path.join(tmpdir(), 'sequence-diagram-audit-test-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' });
  const write = (file, contents) => {
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), contents);
  };
  git('init', '--quiet');
  git('config', 'user.name', 'Diagram audit test');
  git('config', 'user.email', 'diagram-audit@example.invalid');
  git('config', 'core.autocrlf', 'false');
  write('README.md', '# Fixture\n');
  for (const [file, contents] of Object.entries(files)) write(file, contents);
  git('add', '.');
  git('-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'Fixture');
  return { root, base: git('rev-parse', 'HEAD').trim(), git, write };
}

function github(issues = [], comments = []) {
  const state = { issues: structuredClone(issues), comments: structuredClone(comments) };
  const gh = (args, payload) => {
    if (args[0] === 'issue' && args[1] === 'list') return state.issues;
    const endpoint = args[1];
    assert.ok(endpoint.startsWith('repos/example/repo/'), endpoint);
    const methodIndex = args.indexOf('--method');
    const method = methodIndex < 0 ? 'GET' : args[methodIndex + 1];
    if (endpoint.endsWith('/issues') && method === 'POST') {
      const issue = { number: 7, ...payload };
      state.issues.push(issue);
      return issue;
    }
    if (endpoint.endsWith('/issues/7/comments') && method === 'GET') {
      assert.ok(args.includes('--paginate'));
      assert.ok(args.includes('--slurp'));
      return [state.comments];
    }
    if (endpoint.endsWith('/issues/7/comments') && method === 'POST') {
      const comment = { id: 100 + state.comments.length, user: { login: 'github-actions[bot]' }, ...payload };
      state.comments.push(comment);
      return comment;
    }
    if (endpoint.includes('/issues/comments/') && method === 'PATCH') {
      const id = Number(endpoint.split('/').at(-1));
      const comment = state.comments.find(item => item.id === id);
      assert.ok(comment, `Unknown comment ${id}`);
      Object.assign(comment, payload);
      return comment;
    }
    assert.fail(`Unexpected GitHub operation: ${args.join(' ')}`);
  };
  return { state, gh };
}

test('no matching diagrams produces an empty discovery', t => {
  const fixture = repository(t, {});
  assert.deepEqual(discoverDiagrams(fixture.root, fixture.base), []);
});

test('discovery includes only tracked, top-level sequence-diagram markdown', t => {
  const fixture = repository(t, {
    [diagram]: initialDiagram,
    'docs/nested/other-sequence-diagram.md': initialDiagram,
    'docs/other.md': '# Other\n',
  });
  fixture.write('docs/new-sequence-diagram.md', initialDiagram);
  assert.deepEqual(discoverDiagrams(fixture.root, fixture.base), [diagram]);
});

test('unchanged diagrams need no publication', t => {
  const fixture = repository(t);
  assert.deepEqual(validateChanges(fixture.root, fixture.base), []);
  const result = publishChanges({
    ...fixture, repo: 'example/repo', gh: () => assert.fail('No GitHub call expected'),
  });
  assert.equal(result.changed, 0);
});

test('modified diagrams are accepted without changing their bytes', t => {
  const fixture = repository(t);
  const updated = initialDiagram.replace('login', 'auth login');
  fixture.write(diagram, updated);
  assert.deepEqual(validateChanges(fixture.root, fixture.base), [diagram]);
  assert.equal(readFileSync(path.join(fixture.root, diagram), 'utf8'), updated);
});

for (const staged of [false, true]) {
  test(`out-of-scope ${staged ? 'staged' : 'unstaged'} changes are rejected`, t => {
    const fixture = repository(t);
    fixture.write('README.md', '# Unexpected edit\n');
    if (staged) fixture.git('add', 'README.md');
    assert.throws(() => validateChanges(fixture.root, fixture.base), /outside|scope/i);
  });
}

test('untracked files are rejected, including new diagrams', t => {
  const fixture = repository(t);
  fixture.write('docs/new-sequence-diagram.md', initialDiagram);
  assert.throws(() => validateChanges(fixture.root, fixture.base), /untracked/i);
});

test('deleted diagrams are rejected', t => {
  const fixture = repository(t);
  rmSync(path.join(fixture.root, diagram));
  assert.throws(() => validateChanges(fixture.root, fixture.base), /deleted|modified|scope/i);
});

test('removing an existing UTF-8 BOM is rejected', t => {
  const fixture = repository(t, { [diagram]: '\uFEFF' + initialDiagram });
  fixture.write(diagram, initialDiagram);
  assert.throws(() => validateChanges(fixture.root, fixture.base), /BOM|encoding/i);
});

test('an invalid base revision fails instead of becoming a no-op', t => {
  const fixture = repository(t);
  assert.throws(() => discoverDiagrams(fixture.root, 'missing-revision'));
});

test('drift creates an issue and a comment with a diff and audited revision', t => {
  const fixture = repository(t);
  fixture.write(diagram, initialDiagram.replace('login', 'auth login'));
  const api = github();
  publishChanges({ ...fixture, ...api, repo: 'example/repo', summary: `${diagram}: corrected login` });
  assert.equal(api.state.issues.length, 1);
  assert.equal(api.state.issues[0].title, 'docs: sequence diagram drift detected');
  assert.equal(api.state.comments.length, 1);
  const body = api.state.comments[0].body;
  assert.ok(body.includes('<!-- sequence-diagram-audit:docs%2Fauth-sequence-diagram.md -->'));
  assert.ok(body.includes(fixture.base));
  assert.ok(body.includes('+    User->>CLI: auth login'));
  assert.ok(body.includes('corrected login'));
});

test('repeated drift does not duplicate issues or comments', t => {
  const fixture = repository(t);
  fixture.write(diagram, initialDiagram.replace('login', 'auth login'));
  const api = github();
  const options = { ...fixture, ...api, repo: 'example/repo' };
  publishChanges(options);
  const before = structuredClone(api.state);
  publishChanges(options);
  assert.deepEqual(api.state, before);
});

test('changed drift refreshes the existing bot comment', t => {
  const fixture = repository(t);
  fixture.write(diagram, initialDiagram.replace('login', 'auth login'));
  const api = github();
  const options = { ...fixture, ...api, repo: 'example/repo' };
  publishChanges(options);
  const id = api.state.comments[0].id;
  fixture.write(diagram, initialDiagram.replace('login', 'auth status'));
  publishChanges(options);
  assert.equal(api.state.comments.length, 1);
  assert.equal(api.state.comments[0].id, id);
  assert.ok(api.state.comments[0].body.includes('+    User->>CLI: auth status'));
});

test('human comments mentioning a diagram cannot suppress its drift report', t => {
  const fixture = repository(t);
  fixture.write(diagram, initialDiagram.replace('login', 'auth login'));
  const human = {
    id: 1, user: { login: 'maintainer' },
    body: '<!-- sequence-diagram-audit:docs%2Fauth-sequence-diagram.md -->\nPlease check this diagram.',
  };
  const api = github([{ number: 7, title: 'docs: sequence diagram drift detected' }], [human]);
  publishChanges({ ...fixture, ...api, repo: 'example/repo' });
  assert.deepEqual(api.state.comments[0], human);
  assert.equal(api.state.comments.length, 2);
});

test('legacy bot comments are updated instead of duplicated', t => {
  const fixture = repository(t);
  fixture.write(diagram, initialDiagram.replace('login', 'auth login'));
  const api = github([{ number: 7, title: 'docs: sequence diagram drift detected' }], [
    { id: 1, user: { login: 'github-actions[bot]' }, body: `### \`${diagram}\`\n\nOld patch` },
  ]);
  publishChanges({ ...fixture, ...api, repo: 'example/repo' });
  assert.equal(api.state.comments.length, 1);
  assert.ok(api.state.comments[0].body.includes('+    User->>CLI: auth login'));
});

test('GitHub errors surface instead of reporting success', t => {
  const fixture = repository(t);
  fixture.write(diagram, initialDiagram.replace('login', 'auth login'));
  assert.throws(() => publishChanges({
    ...fixture, repo: 'example/repo', gh: () => { throw new Error('GitHub unavailable'); },
  }), /GitHub unavailable/);
});
