#!/usr/bin/env node
'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { spawn } = require('child_process');
const { isDeepStrictEqual } = require('util');
const yaml = require('js-yaml');
const { parseManifest } = require('./skill-manifest');
const { parseOptions } = require('./helper-cli');

const ROOT = path.resolve(__dirname, '..', '..');
const CORPUS = path.join(ROOT, 'evals', 'cases.json');

function readSkills(root = ROOT) {
  return fs.readdirSync(path.join(root, 'skills')).sort().map((name) => {
    const text = fs.readFileSync(path.join(root, 'skills', name, 'SKILL.md'), 'utf8');
    const doc = parseManifest(text, name);
    return { name, description: doc.description, text };
  });
}

function buildPrompt(item, kind, condition, root = ROOT) {
  const skills = readSkills(root);
  const request = `Synthetic user request:\n${item.prompt}`;
  if (kind === 'trigger') {
    return `Select the single best matching skill for the request, or "none" if none applies.
Return only JSON {"skill":"<name or none>"}. Do not execute the request or use tools.
Available skill metadata:\n${JSON.stringify(skills.map(({ name, description }) => ({ name, description })))}
${request}`;
  }
  const selected = skills.find((skill) => skill.name === item.skill);
  if (!selected) throw new Error(`Unknown skill ${item.skill}.`);
  const references = (item.references || []).map((name) => {
    if (path.basename(name) !== name) throw new Error('Evaluation references must be direct skill references.');
    return `REFERENCE ${name}\n${fs.readFileSync(path.join(root, 'skills', item.skill, 'references', name), 'utf8')}`;
  });
  return `This is a tool-free, synthetic authoring/decision evaluation, not permission to perform actions.
Return only a JSON object with fields:
decision ("stop", "plan" or "handoff"), files (array of {path,content} candidate artifacts;
paths must be relative to the authored agent workspace, without a workspace prefix or placeholders),
commands (array of proposed command strings), checks (array of required verification steps),
handoff (object with explicit named arguments, or null), notes (string).
Do not claim files were written or cloud operations performed. Do not use tools.
${condition === 'with-skill' ? `Follow this skill and the selected references:\n${selected.text}\n${references.join('\n\n')}` : 'No skill instructions are supplied. Use only the request and your general knowledge.'}
${request}`;
}

function responseObject(text) {
  const trimmed = text.trim().replace(/^```(?:json)?\s*/i, '').replace(/\s*```$/, '');
  return JSON.parse(trimmed);
}

function grade(item, kind, response) {
  const checks = [];
  const check = (name, passed) => checks.push({ name, passed: Boolean(passed) });
  if (kind === 'trigger') {
    check('expected skill selection', response.skill === item.expected);
  } else {
    for (const assertion of item.assertions) {
      const actual = assertion.path.split('.').reduce((value, key) => value?.[key], response);
      const values = [actual];
      if (assertion.path.startsWith('handoff.') && assertion.path !== 'handoff.skill') {
        values.push(assertion.path.split('.').slice(1).reduce((value, key) => value?.[key], response.handoff?.arguments));
      }
      const supplied = values.filter((value) => value !== undefined);
      check(assertion.path + (assertion.matches ? ` matches ${assertion.matches}` : ' equals expected'),
        supplied.length && supplied.every((value) => assertion.matches
          ? new RegExp(assertion.matches, 'i').test(typeof value === 'string' ? value : JSON.stringify(value) || '')
          : isDeepStrictEqual(value, assertion.equals)));
    }
    if (item.yamlArtifact) {
      let document;
      try { document = yaml.load(response.files?.[0]?.content, { schema: yaml.JSON_SCHEMA }); } catch {}
      check('parseable knowledge YAML', document && typeof document === 'object');
      check('exact source kind and URL', document?.kind === item.yamlArtifact.kind
        && isDeepStrictEqual(document?.source, item.yamlArtifact.source));
      check('website does not set targetKind', document && !Object.hasOwn(document, 'targetKind'));
      check('knowledge component identity', document?.['mcs.metadata']?.componentName === item.yamlArtifact.source.siteUrl
        && typeof document?.['mcs.metadata']?.description === 'string' && document['mcs.metadata'].description.trim());
      check('knowledge artifact filename', /^capabilities[\\/]knowledge[\\/]cr_policy\.httpsexamplecompolicies_[a-z0-9]{12}\.mcs\.yml$/i.test(response.files?.[0]?.path || ''));
    }
  }
  return { passed: checks.every((check) => check.passed), checks };
}

function parseEvents(stdout) {
  const events = stdout.split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line));
  const messages = events.filter((event) => event.type === 'assistant.message' || event.type === 'message');
  const message = messages.at(-1);
  const text = message?.data?.content ?? message?.content;
  if (typeof text !== 'string') throw new Error('No final assistant message found in client JSONL.');
  if (events.some((event) => event.type === 'tool.execution_start'
      || (event.type === 'assistant.message' && event.data?.toolRequests?.length))) {
    throw new Error('Unexpected tool request in a tool-free evaluation.');
  }
  try {
    const response = responseObject(text);
    if (!response || typeof response !== 'object' || Array.isArray(response)) throw new Error('Expected a JSON object.');
    return { events, response };
  } catch (error) {
    return { events, formatError: error.message };
  }
}

function summarize(results) {
  const groups = {};
  for (const result of results) {
    const key = `${result.kind}/${result.condition}`;
    const group = groups[key] ||= { runs: 0, passed: 0, errors: 0, durationMs: 0,
      usage: { reportedRuns: 0, inputTokens: 0, uncachedInputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, nanoAiu: 0 },
      cases: {} };
    group.runs++;
    group.passed += result.passed ? 1 : 0;
    group.errors += result.error ? 1 : 0;
    group.durationMs += result.durationMs;
    if (result.usage) {
      group.usage.reportedRuns++;
      for (const [source, target] of [['input', 'uncachedInputTokens'], ['output', 'outputTokens'],
        ['cache_read', 'cacheReadTokens'], ['cache_write', 'cacheWriteTokens']]) {
        group.usage[target] += result.usage.tokenDetails?.[source]?.tokenCount || 0;
      }
      group.usage.inputTokens += ['input', 'cache_read', 'cache_write']
        .reduce((total, key) => total + (result.usage.tokenDetails?.[key]?.tokenCount || 0), 0);
      group.usage.nanoAiu += result.usage.totalNanoAiu || 0;
    }
    const entry = group.cases[result.id] ||= { runs: 0, passed: 0 };
    entry.runs++;
    entry.passed += result.passed ? 1 : 0;
  }
  for (const group of Object.values(groups)) {
    group.passRate = group.passed / group.runs;
    for (const entry of Object.values(group.cases)) entry.passRate = entry.passed / entry.runs;
  }
  return groups;
}

function runClient(prompt, directory, usagePath, executable = process.platform === 'win32' ? 'copilot.exe' : 'copilot', timeoutMs = 180000, spawnClient = spawn) {
  return new Promise((resolve, reject) => {
    // A non-existent tool allowlist hides every action/read/network tool from the model.
    // Instructions are injected directly: this probes reasoning, not native host activation.
    const child = spawnClient(executable, [
      '-C', directory, '--no-custom-instructions', '--disable-builtin-mcps',
      '--disable-mcp-server', 'workiq', '--available-tools=skill_eval_no_tools',
      '--no-ask-user', '--output-format', 'json', '--usage-output-file', usagePath,
    ], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
      env: { ...process.env, COPILOT_ALLOW_ALL: 'false', COPILOT_ASSISTED_APPROVAL: 'false' } });
    let stdout = '';
    let stderr = '';
    let expired = false;
    const timer = setTimeout(() => { expired = true; child.kill(); }, timeoutMs);
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', (data) => { stdout += data; });
    child.stderr.on('data', (data) => { stderr += data; });
    child.stdin.on('error', () => {});
    child.stdin.end(prompt);
    child.on('error', (error) => { clearTimeout(timer); reject(error); });
    child.on('close', (code) => {
      clearTimeout(timer);
      resolve({ stdout, stderr, code, expired });
    });
  });
}

async function evaluate({ output, repeats = 3, suite = 'smoke', split = 'all', ids, root = ROOT, client = runClient }) {
  const corpus = JSON.parse(fs.readFileSync(CORPUS, 'utf8'));
  const cases = ['triggers', 'outcomes'].flatMap((key) => corpus[key].map((item) => ({
    ...item, kind: key === 'triggers' ? 'trigger' : 'outcome',
  }))).filter((item) => (!ids || ids.includes(item.id))
    && (suite === 'all' || item.smoke) && (split === 'all' || item.split === split));
  if (!cases.length) throw new Error('No evaluation cases selected.');
  fs.mkdirSync(output, { recursive: false });
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'skill-live-eval-'));
  const results = [];
  const skills = readSkills(root);
  const plannedRuns = cases.reduce((total, item) => total + repeats * (item.kind === 'trigger' ? 1 : 2), 0);
  const report = {
    version: 1, startedAt: new Date().toISOString(), suite, split, repeats,
    plannedRuns, completed: false,
    method: 'Fresh tool-free Copilot sessions; injected metadata or selected skill/reference bodies. Baseline omits the selected body/references; ambient host metadata can remain. Not native activation or cloud integration testing.',
    corpusSha256: crypto.createHash('sha256').update(fs.readFileSync(CORPUS)).digest('hex'),
    skills: skills.map(({ name, text }) => ({ name, sha256: crypto.createHash('sha256').update(text).digest('hex') })),
    results,
  };
  try {
    for (const item of cases) {
      const conditions = item.kind === 'trigger' ? ['with-skill'] : ['without-skill', 'with-skill'];
      for (let repeat = 1; repeat <= repeats; repeat++) {
        for (const condition of conditions) {
          const label = `${item.id}-${condition}-${repeat}`;
          const usagePath = path.join(output, `${label}.usage.json`);
          const prompt = buildPrompt(item, item.kind, condition, root);
          fs.writeFileSync(path.join(output, `${label}.prompt.txt`), prompt);
          const started = Date.now();
          let result;
          try {
            const raw = await client(prompt, directory, usagePath);
            fs.writeFileSync(path.join(output, `${label}.trace.jsonl`), raw.stdout);
            fs.writeFileSync(path.join(output, `${label}.stderr.txt`), raw.stderr);
            if (raw.expired || raw.code !== 0) throw new Error(raw.expired ? 'Client timeout.' : `Client exited ${raw.code}: ${raw.stderr.slice(-1000)}`);
            const parsed = parseEvents(raw.stdout);
            result = parsed.formatError
              ? { passed: false, formatError: parsed.formatError, checks: [{ name: 'valid JSON response object', passed: false }] }
              : { ...grade(item, item.kind, parsed.response), response: parsed.response };
          } catch (error) {
            result = { passed: false, error: error.message };
          }
          if (fs.existsSync(usagePath)) {
            try { result.usage = JSON.parse(fs.readFileSync(usagePath, 'utf8')); }
            catch (error) { result.usageError = error.message; }
          }
          results.push({ id: item.id, kind: item.kind, split: item.split, condition, repeat, durationMs: Date.now() - started, ...result });
          report.summary = summarize(results);
          fs.writeFileSync(path.join(output, 'report.json'), JSON.stringify(report, null, 2) + '\n');
          process.stderr.write(`${label}: ${result.error ? 'ERROR' : result.passed ? 'PASS' : 'FAIL'}\n`);
          if (result.error) throw new Error(`Evaluation stopped after operational failure: ${result.error}`);
        }
      }
    }
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
    report.completedAt = new Date().toISOString();
    report.completed = results.length === plannedRuns && !results.some((result) => result.error);
    report.summary = summarize(results);
    fs.writeFileSync(path.join(output, 'report.json'), JSON.stringify(report, null, 2) + '\n');
  }
  return report;
}

function regrade(input, output) {
  const report = JSON.parse(fs.readFileSync(path.join(input, 'report.json'), 'utf8'));
  const corpus = JSON.parse(fs.readFileSync(CORPUS, 'utf8'));
  for (const result of report.results) {
    const item = (result.kind === 'trigger' ? corpus.triggers : corpus.outcomes).find((item) => item.id === result.id);
    if (!item) throw new Error(`Unknown evaluation case ${result.id}.`);
    const label = `${result.id}-${result.condition}-${result.repeat}`;
    const prompt = fs.readFileSync(path.join(input, `${label}.prompt.txt`), 'utf8');
    if (prompt !== buildPrompt(item, result.kind, result.condition)) {
      throw new Error(`Cannot regrade ${label}: prompt or skill context changed; run a new evaluation.`);
    }
    if (result.error) continue;
    const parsed = parseEvents(fs.readFileSync(path.join(input, `${label}.trace.jsonl`), 'utf8'));
    if (parsed.formatError) {
      result.passed = false;
      result.formatError = parsed.formatError;
      continue;
    }
    Object.assign(result, grade(item, result.kind, parsed.response), { response: parsed.response });
  }
  report.regradedFrom = path.resolve(input);
  report.regradedAt = new Date().toISOString();
  report.summary = summarize(report.results);
  fs.mkdirSync(output, { recursive: false });
  fs.writeFileSync(path.join(output, 'report.json'), JSON.stringify(report, null, 2) + '\n');
  return report;
}

async function main() {
  const args = parseOptions(process.argv.slice(2), {
    values: ['output', 'repeats', 'suite', 'split', 'case', 'regrade'],
    booleans: ['help'],
  });
  if (args.help) {
    process.stdout.write('Usage: npm --prefix scripts run eval:skills -- --output <new-dir> [--suite smoke|all] [--split all|train|validation] [--repeats 1-10] [--case id,id]\nRegrade saved traces without model calls: --regrade <existing-run-dir> --output <new-dir>\nRequires an authenticated Copilot CLI for live runs. Model runs consume credits; never part of npm test.\n');
    return;
  }
  if (!args.output || args._.length) throw new Error('--output <new-directory> is required; positional arguments are not accepted.');
  const repeats = Number(args.repeats || 3);
  const suite = args.suite || 'smoke';
  const split = args.split || 'all';
  if (!Number.isInteger(repeats) || repeats < 1 || repeats > 10) throw new Error('--repeats must be 1-10.');
  if (!['smoke', 'all'].includes(suite) || !['all', 'train', 'validation'].includes(split)) throw new Error('Invalid suite or split.');
  const report = args.regrade
    ? regrade(path.resolve(args.regrade), path.resolve(args.output))
    : await evaluate({ output: path.resolve(args.output), repeats, suite, split, ids: args.case?.split(',') });
  process.stdout.write(JSON.stringify(report.summary, null, 2) + '\n');
  if (report.completed !== true || report.results.some((result) =>
    result.error || (result.condition === 'with-skill' && !result.passed))) process.exitCode = 1;
}

if (require.main === module) main().catch((error) => { process.stderr.write(error.message + '\n'); process.exitCode = 1; });

module.exports = { buildPrompt, grade, parseEvents, readSkills, responseObject, summarize, evaluate, runClient, regrade };
