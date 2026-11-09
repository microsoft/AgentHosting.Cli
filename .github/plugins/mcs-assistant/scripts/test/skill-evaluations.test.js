const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");
const { EventEmitter } = require("node:events");
const { PassThrough } = require("node:stream");
const { spawnSync } = require("node:child_process");
const corpus = require("../../evals/cases.json");
const { buildPrompt, grade, parseEvents, readSkills, summarize, evaluate, runClient, regrade } = require("../src/evaluate-skills");

test("trigger corpus covers all skills, near misses and held-out examples", () => {
  for (const skill of readSkills()) {
    for (const split of ["train", "validation"]) {
      assert.ok(corpus.triggers.some((item) => item.expected === skill.name && item.split === split));
    }
  }
  assert.ok(corpus.triggers.filter((item) => item.expected === "none").length >= 4);
  assert.equal(new Set([...corpus.triggers, ...corpus.outcomes].map((item) => item.id)).size,
    corpus.triggers.length + corpus.outcomes.length);
});

test("baseline omits skill body and references while retaining the same request", () => {
  const item = corpus.outcomes.find((item) => item.id === "knowledge-website");
  const enabled = buildPrompt(item, "outcome", "with-skill");
  const baseline = buildPrompt(item, "outcome", "without-skill");
  assert.ok(enabled.includes(item.prompt) && baseline.includes(item.prompt));
  assert.match(enabled, /Re-read and parse every written YAML artifact/);
  assert.match(enabled, /REFERENCE website\.md/);
  assert.match(enabled, /paths must be relative to the authored agent workspace/);
  assert.ok(!baseline.includes("REFERENCE website.md"));
  assert.ok(!baseline.includes("Re-read and parse every written YAML artifact"));
});

test("artifact grading validates YAML semantically without depending on key order", () => {
  const item = corpus.outcomes.find((item) => item.id === "knowledge-website");
  const response = {
    decision: "plan",
    files: [{
      path: "capabilities\\knowledge\\cr_policy.httpsexamplecompolicies_012345abcdef.mcs.yml",
      content: [
        "kind: KnowledgeSourceConfiguration",
        "source:",
        "  siteUrl: https://example.com/policies",
        "  kind: WebsiteKnowledgeSource",
        "mcs.metadata:",
        "  componentName: https://example.com/policies",
        "  description: Public policy information",
      ].join("\n"),
    }],
    checks: ["Parse YAML", "Review diff"],
    notes: "Local proposal only.",
  };
  assert.equal(grade(item, "outcome", response).passed, true);
  response.files[0].content += "\ntargetKind: Wrong\n";
  assert.equal(grade(item, "outcome", response).passed, false);
  response.files[0].content = "kind: [unclosed";
  assert.equal(grade(item, "outcome", response).passed, false);
});

test("literal-import grading rejects expanding paths and missing dry-run", () => {
  const item = corpus.outcomes.find((item) => item.id === "skill-import-literal");
  const command = "node 'C:\\plugins\\$cache\\add-skill.bundle.js' import " +
    "--src 'C:\\fixtures\\O''Brien\\$target\\demo' --workspace 'C:\\fixtures\\agent' --dry-run";
  assert.equal(grade(item, "outcome", { decision: "plan", commands: [command] }).passed, true);
  for (const invalid of [
    command.replace("'C:\\plugins\\$cache\\add-skill.bundle.js'", '"C:\\plugins\\$cache\\add-skill.bundle.js"'),
    command.replace("O''Brien", "O'Brien"),
    command.replace(" --dry-run", ""),
  ]) {
    assert.equal(grade(item, "outcome", { decision: "plan", commands: [invalid] }).passed, false);
  }
});

test("cloud-target grading accepts deployment plans and rejects refusal or cloud IDs used as local IDs", () => {
  for (const skill of ["push", "publish"]) {
    const item = corpus.outcomes.find((item) => item.id === `${skill}-cloud-target-supported`);
    const command = "msagent agent deploy --project 'C:\\fixtures\\project' " +
      "--agent-id '33333333-3333-3333-3333-333333333333' " +
      "--deployment-id '44444444-4444-4444-4444-444444444444' " +
      (skill === "publish" ? "--publish " : "") + "--json --non-interactive";
    assert.equal(grade(item, "outcome", { decision: "plan", commands: [command] }).passed, true);
    assert.equal(grade(item, "outcome", { decision: "stop", commands: [] }).passed, false);
    assert.equal(grade(item, "outcome", {
      decision: "plan",
      commands: [command.replace("33333333-3333-3333-3333-333333333333", "22222222-2222-2222-2222-222222222222")],
    }).passed, false);
    const wrongMode = skill === "publish" ? command.replace("--publish ", "") : command + " --publish";
    assert.equal(grade(item, "outcome", { decision: "plan", commands: [wrongMode] }).passed, false);
  }
});

test("handoff grading accepts nested arguments but rejects omitted or conflicting target identities", () => {
  const item = corpus.outcomes.find((item) => item.id === "creation-publication");
  const targets = {
    projectRoot: "C:\\fixtures\\project",
    agentId: "33333333-3333-3333-3333-333333333333",
    deploymentId: "44444444-4444-4444-4444-444444444444",
    schemaName: "cr_support",
    environmentId: "11111111-1111-1111-1111-111111111111",
    mcsAgentId: "22222222-2222-2222-2222-222222222222",
  };
  for (const nested of [false, true]) {
    const response = {
      decision: "handoff", commands: [], checks: ["Require final publication confirmation."],
      handoff: { skill: "mcs-assistant:publish-agent", ...(nested ? { arguments: { ...targets } } : targets) },
    };
    assert.equal(grade(item, "outcome", response).passed, true);
    for (const key of Object.keys(targets)) {
      const missing = structuredClone(response);
      delete (nested ? missing.handoff.arguments : missing.handoff)[key];
      assert.equal(grade(item, "outcome", missing).passed, false, `missing ${key}, nested=${nested}`);
      const incorrect = structuredClone(response);
      (nested ? incorrect.handoff.arguments : incorrect.handoff)[key] = "wrong-target";
      assert.equal(grade(item, "outcome", incorrect).passed, false, `incorrect ${key}, nested=${nested}`);
      const conflicting = structuredClone(response);
      if (nested) conflicting.handoff[key] = "wrong-target";
      else conflicting.handoff.arguments = { [key]: "wrong-target" };
      assert.equal(grade(item, "outcome", conflicting).passed, false, `conflicting ${key}, nested=${nested}`);
    }
  }
});

test("JSONL parser accepts final responses but rejects tool-bearing or malformed traces", () => {
  const line = JSON.stringify({ type: "assistant.message", data: { content: '{"skill":"push-agent"}', toolRequests: [] } });
  assert.equal(parseEvents(line).response.skill, "push-agent");
  assert.throws(() => parseEvents('not JSON'), SyntaxError);
  assert.ok(parseEvents(JSON.stringify({ type: "assistant.message", data: { content: "Not JSON." } })).formatError);
  assert.ok(parseEvents(JSON.stringify({ type: "assistant.message", data: { content: "null" } })).formatError);
  assert.throws(() => parseEvents(line + "\n" + JSON.stringify({ type: "tool.execution_start" })), /tool request/);
  assert.throws(() => parseEvents(JSON.stringify({ type: "result" })), /No final assistant/);
});

test("client sends multiline prompts on stdin rather than through the Windows launcher", async () => {
  const prompt = "Line one.\nLine two with \"quotes\" and a C:\\fixture path.";
  let received;
  const spawnClient = (exe, args, opts) => {
    assert.ok(!args.includes("--prompt"));
    assert.ok(!args.includes(prompt));
    assert.ok(args.includes("--available-tools=skill_eval_no_tools"));
    assert.equal(opts.env.COPILOT_ALLOW_ALL, "false");
    const child = new EventEmitter();
    child.stdout = new PassThrough();
    child.stderr = new PassThrough();
    child.stdin = new EventEmitter();
    child.stdin.end = (value) => {
      received = value;
      queueMicrotask(() => { child.stdout.emit("data", "{}"); child.emit("close", 0); });
    };
    return child;
  };
  const result = await runClient(prompt, "fixture", "usage.json", "copilot", 1000, spawnClient);
  assert.equal(received, prompt);
  assert.equal(result.code, 0);
});

test("client preserves UTF-8 characters split across stdout and stderr chunks", async () => {
  const stdout = JSON.stringify({ notes: "\u20ac \ud83d\ude00" });
  const stderr = "Progress: \u20ac \ud83d\ude00";
  const spawnClient = () => {
    const child = new EventEmitter();
    child.stdout = new PassThrough();
    child.stderr = new PassThrough();
    child.stdin = new EventEmitter();
    child.stdin.end = () => queueMicrotask(() => {
      for (const [stream, text] of [[child.stdout, stdout], [child.stderr, stderr]]) {
        for (const byte of Buffer.from(text)) stream.write(Buffer.from([byte]));
        stream.end();
      }
      child.emit("close", 0);
    });
    return child;
  };
  const result = await runClient("fixture", "fixture", "usage.json", "copilot", 1000, spawnClient);
  assert.equal(result.stdout, stdout);
  assert.equal(result.stderr, stderr);
});

test("evaluation runner persists repeated baseline comparisons and usage without live calls", async (t) => {
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), "eval-test-"));
  t.after(() => fs.rmSync(parent, { recursive: true, force: true }));
  const output = path.join(parent, "results");
  let calls = 0;
  const client = async (prompt, cwd, usage) => {
    calls++;
    assert.deepEqual(fs.readdirSync(cwd), []);
    fs.writeFileSync(usage, JSON.stringify({ tokenDetails: { input: { tokenCount: 10 }, output: { tokenCount: 5 } }, totalNanoAiu: 123 }));
    const response = prompt.includes("No skill instructions")
      ? { decision: "plan", files: [], notes: "Unknown." }
      : { decision: "stop", files: [], notes: "Unsupported template." };
    return { stdout: JSON.stringify({ type: "assistant.message", data: { content: JSON.stringify(response) } }), stderr: "", code: 0 };
  };
  const report = await evaluate({ output, repeats: 2, ids: ["knowledge-unsupported"], client });
  assert.equal(calls, 4);
  assert.equal(report.summary["outcome/with-skill"].passRate, 1);
  assert.equal(report.summary["outcome/without-skill"].passRate, 0);
  assert.equal(report.summary["outcome/with-skill"].usage.inputTokens, 20);
  assert.equal(report.summary["outcome/with-skill"].usage.nanoAiu, 246);
  assert.ok(report.completedAt);
  assert.equal(report.completed, true);
  assert.equal(report.plannedRuns, 4);
  assert.equal(JSON.parse(fs.readFileSync(path.join(output, "report.json"), "utf8")).results.length, 4);
  assert.equal(fs.readdirSync(output).filter((file) => file.endsWith(".trace.jsonl")).length, 4);
  assert.equal(summarize([{ id: "missing", kind: "outcome", condition: "with-skill", durationMs: 1, passed: true }])["outcome/with-skill"].usage.reportedRuns, 0);
  const reassessed = regrade(output, path.join(parent, "regraded"));
  assert.equal(calls, 4);
  assert.equal(reassessed.summary["outcome/with-skill"].passRate, 1);
  const cli = spawnSync(process.execPath, [
    path.join(__dirname, "..", "src", "evaluate-skills.js"),
    "--regrade", output, "--output", path.join(parent, "regraded-cli"),
  ], { encoding: "utf8" });
  assert.equal(cli.status, 0, cli.stderr);
  report.completed = false;
  fs.writeFileSync(path.join(output, "report.json"), JSON.stringify(report));
  const incomplete = spawnSync(process.execPath, [
    path.join(__dirname, "..", "src", "evaluate-skills.js"),
    "--regrade", output, "--output", path.join(parent, "regraded-incomplete"),
  ], { encoding: "utf8" });
  assert.equal(incomplete.status, 1, incomplete.stderr);
  const prompt = path.join(output, "knowledge-unsupported-with-skill-1.prompt.txt");
  fs.appendFileSync(prompt, "\nChanged context.");
  assert.throws(() => regrade(output, path.join(parent, "invalid")), /prompt or skill context changed/);
});

test("operational errors stop a live batch and persist an honest incomplete report", async (t) => {
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), "eval-failure-"));
  t.after(() => fs.rmSync(parent, { recursive: true, force: true }));
  const output = path.join(parent, "results");
  let calls = 0;
  await assert.rejects(evaluate({ output, repeats: 3, ids: ["knowledge-unsupported"], client: async () => {
    calls++;
    return { stdout: "", stderr: "Authentication unavailable.", code: 1 };
  } }), /operational failure/);
  assert.equal(calls, 1);
  const report = JSON.parse(fs.readFileSync(path.join(output, "report.json"), "utf8"));
  assert.equal(report.results[0].passed, false);
  assert.equal(report.completed, false);
  assert.equal(report.plannedRuns, 6);
  assert.match(report.results[0].error, /Authentication unavailable/);
  for (const completed of [false, true]) {
    report.completed = completed;
    fs.writeFileSync(path.join(output, "report.json"), JSON.stringify(report));
    const regraded = path.join(parent, `regraded-${completed}`);
    const cli = spawnSync(process.execPath, [
      path.join(__dirname, "..", "src", "evaluate-skills.js"),
      "--regrade", output, "--output", regraded,
    ], { encoding: "utf8" });
    assert.equal(cli.status, 1, cli.stderr);
    const saved = JSON.parse(fs.readFileSync(path.join(regraded, "report.json"), "utf8"));
    assert.equal(saved.results[0].condition, "without-skill");
    assert.match(saved.results[0].error, /Authentication unavailable/);
  }
});

test("usage totals include cached input without confusing it with uncached billing tokens", () => {
  const summary = summarize([{
    id: "cache", kind: "trigger", condition: "with-skill", passed: true, durationMs: 1,
    usage: { tokenDetails: {
      input: { tokenCount: 10 }, cache_read: { tokenCount: 20 },
      cache_write: { tokenCount: 30 }, output: { tokenCount: 5 },
    } },
  }])["trigger/with-skill"].usage;
  assert.equal(summary.inputTokens, 60);
  assert.equal(summary.uncachedInputTokens, 10);
  assert.equal(summary.outputTokens, 5);
});
