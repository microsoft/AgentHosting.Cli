const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");
const { after } = require("node:test");

const {
  BASE_URL,
  SCOPE,
  SKILL_TYPE,
  WorkIqError,
  createRunDirectory,
  downloadEntry,
  listSkills,
  sanitizeFolderName,
  searchSkills,
  signIn,
  startLoopback,
} = require("../src/workiq-catalog");
const { makeZip } = require("./zip-fixture");

const CLIENT_ID = "11111111-2222-3333-4444-555555555555";
const PLUGIN_ID = "urn:air:microsoft.com:mos:T_00000000-0000-0000-0000-000000000001";

const roots = [];
after(() => {
  for (const root of roots) fs.rmSync(root, { recursive: true, force: true });
});

function tempDir() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "workiq-test-"));
  roots.push(root);
  return root;
}

function skillZip(folder, frontmatter, extra = []) {
  return makeZip([
    { name: `skills/${folder}/SKILL.md`, data: `\uFEFF---\n${frontmatter}\n---\n\n# Body\n` },
    ...extra,
  ]);
}

function entry(id, file, overrides = {}) {
  return {
    identifier: `${PLUGIN_ID}:${id}`,
    displayName: "Contoso Sample Plugin",
    description: "Plugin-level description",
    type: SKILL_TYPE,
    version: "1.0.17",
    url: `${BASE_URL}/api/catalogs/agenticResources/artifacts/${file}?version=1.0.17`,
    ...overrides,
  };
}

// Serves the search response and the zips in `files` (keyed by the last URL path segment).
function catalogFetch(entries, files, { searchStatus = 200, searchHeaders = {} } = {}) {
  const calls = [];
  const fetchImpl = async (url, init = {}) => {
    calls.push({ url, headers: init.headers || {}, redirect: init.redirect });
    const u = new URL(url);
    if (u.pathname.endsWith("/plugins")) {
      const body = searchStatus === 200 ? JSON.stringify({ specVersion: "1.0", entries }) : "{}";
      return new Response(body, { status: searchStatus, headers: searchHeaders });
    }
    const file = files[u.pathname.split("/").pop()];
    if (typeof file === "function") return file(url, init);
    if (!file) return new Response("", { status: 404 });
    return new Response(file, { status: 200, headers: { "content-type": "application/zip" } });
  };
  return { fetchImpl, calls };
}

function ctxFor(fetchImpl) {
  return { baseUrl: BASE_URL, token: "token-for-tests", fetchImpl, retryDelayMs: 0, concurrency: 2 };
}

function failingBody(error, streamed) {
  const headers = new Headers({ "x-workiq-request-id": "req-body" });
  if (!streamed) {
    return { ok: true, status: 200, headers, body: null, arrayBuffer: async () => { throw error; } };
  }
  let sent = false;
  return new Response(new ReadableStream({
    pull(controller) {
      if (!sent) {
        sent = true;
        controller.enqueue(Buffer.from("partial-body"));
      } else {
        controller.error(error);
      }
    },
  }), { headers });
}

test("lists skill bundles with names from SKILL.md, ready to import", async () => {
  const dest = tempDir();
  const { fetchImpl, calls } = catalogFetch(
    [
      entry("b", "kql"),
      entry("a", "sentinel"),
      { ...entry("p", "plugin"), type: "application/agent-plugins+zip" },
    ],
    {
      kql: skillZip("kql-sample-skill", "name: kql-sample-skill\ndescription: Use when writing KQL.", [
        { name: "skills/kql-sample-skill/kql-reference.md", data: "ref" },
      ]),
      sentinel: skillZip("sample-guidance-skill", "name: sample-guidance-skill\ndescription: 'Invoke for Contoso: sample guidance.'"),
    }
  );

  const result = await listSkills(ctxFor(fetchImpl), { destRoot: dest });

  assert.equal(result.available, 2);
  assert.equal(result.count, 2);
  assert.deepEqual(result.failed, []);
  assert.deepEqual(result.skills.map((s) => [s.n, s.name, s.description, s.fileCount]), [
    [1, "kql-sample-skill", "Use when writing KQL.", 2],
    [2, "sample-guidance-skill", "Invoke for Contoso: sample guidance.", 1],
  ]);
  assert.deepEqual(result.skills[0].plugin, { name: "Contoso Sample Plugin", version: "1.0.17" });
  for (const skill of result.skills) {
    assert.equal(path.basename(skill.dir), skill.name);
    assert.ok(fs.existsSync(path.join(skill.dir, "SKILL.md")));
  }
  assert.ok(fs.existsSync(path.join(result.skills[0].dir, "kql-reference.md")));
  assert.ok(!calls.some((c) => c.url.includes("/plugin?")), "non-skill entries are not downloaded");
  assert.ok(calls.every((c) => c.headers.Authorization === "Bearer token-for-tests"));

  const filtered = await listSkills(ctxFor(fetchImpl), { destRoot: dest, query: "KQL" });
  assert.deepEqual(filtered.skills.map((s) => [s.n, s.name]), [[1, "kql-sample-skill"]]);
  assert.equal(filtered.downloaded, 2);
  assert.notEqual(filtered.skills[0].dir, result.skills[0].dir);
  assert.ok(fs.existsSync(path.join(result.skills[0].dir, "SKILL.md")), "earlier acquisitions are preserved");
});

test("uses exclusive run directories and ignores pre-existing hash-directory junctions", async () => {
  const parent = tempDir();
  const firstRun = createRunDirectory(parent);
  const secondRun = createRunDirectory(parent);
  assert.notEqual(firstRun, secondRun);
  if (typeof process.getuid === "function") {
    assert.equal(fs.statSync(firstRun).mode & 0o077, 0);
  }

  const victim = tempDir();
  const sentinel = path.join(victim, "unrelated.txt");
  fs.writeFileSync(sentinel, "must survive");
  const item = entry("safe", "safe");
  const hash = crypto.createHash("sha256").update(item.identifier).digest("hex").slice(0, 12);
  const junction = path.join(firstRun, hash);
  fs.symlinkSync(victim, junction, process.platform === "win32" ? "junction" : "dir");
  try {
    const { fetchImpl } = catalogFetch([item], { safe: skillZip("safe", "name: safe") });
    const result = await listSkills(ctxFor(fetchImpl), { destRoot: firstRun });
    assert.equal(result.count, 1);
    assert.equal(fs.readFileSync(sentinel, "utf8"), "must survive");
    assert.deepEqual(fs.readdirSync(victim), ["unrelated.txt"]);
    assert.ok(fs.existsSync(path.join(result.skills[0].dir, "SKILL.md")));
    assert.notEqual(path.dirname(result.skills[0].dir), junction);
  } finally {
    fs.unlinkSync(junction);
  }
});

test("rejects a destination junction before downloading or changing its target", async () => {
  const parent = tempDir();
  const victim = tempDir();
  const junction = path.join(parent, "destination");
  fs.symlinkSync(victim, junction, process.platform === "win32" ? "junction" : "dir");
  try {
    assert.throws(() => createRunDirectory(junction), { kind: "invalid-input" });
    await assert.rejects(downloadEntry(
      ctxFor(async () => assert.fail("must not request an archive for an unsafe directory")),
      entry("unsafe", "unsafe"),
      junction
    ), { kind: "invalid-input" });
    assert.deepEqual(fs.readdirSync(victim), []);
  } finally {
    fs.unlinkSync(junction);
  }
});

for (const missing of [false, true]) {
  test(`uses canonical paths when an ancestor link changes before ${missing ? "parent" : "run"} creation`, (t) => {
    const initial = tempDir();
    const alternate = tempDir();
    const junction = path.join(tempDir(), "ancestor");
    fs.mkdirSync(path.join(initial, "downloads"));
    fs.mkdirSync(path.join(alternate, "downloads"));
    fs.symlinkSync(initial, junction, process.platform === "win32" ? "junction" : "dir");
    const segments = missing ? ["downloads", "nested", "new-parent"] : ["downloads"];
    const expectedParent = path.join(fs.realpathSync(initial), ...segments);
    const method = missing ? "mkdirSync" : "mkdtempSync";
    const original = fs[method];
    let replaced = false;
    t.mock.method(fs, method, (...args) => {
      if (!replaced) {
        fs.unlinkSync(junction);
        fs.symlinkSync(alternate, junction, process.platform === "win32" ? "junction" : "dir");
        replaced = true;
      }
      return original(...args);
    });
    try {
      const run = createRunDirectory(path.join(junction, ...segments));
      assert.equal(replaced, true);
      assert.equal(path.dirname(run), expectedParent);
      assert.equal(fs.realpathSync(run), run);
      assert.deepEqual(fs.readdirSync(path.join(alternate, "downloads")), []);
    } finally {
      fs.unlinkSync(junction);
    }
  });
}

test("refuses a missing parent replaced by a link during creation", (t) => {
  const parent = tempDir();
  const alternate = tempDir();
  const link = path.join(parent, "missing");
  const original = fs.mkdirSync;
  let inserted = false;
  t.mock.method(fs, "mkdirSync", (...args) => {
    if (!inserted) {
      fs.symlinkSync(alternate, args[0], process.platform === "win32" ? "junction" : "dir");
      inserted = true;
    }
    return original(...args);
  });
  try {
    assert.throws(() => createRunDirectory(path.join(link, "child")), { kind: "invalid-input" });
    assert.equal(inserted, true);
    assert.deepEqual(fs.readdirSync(alternate), []);
  } finally {
    if (inserted) fs.unlinkSync(link);
  }
});

test("rejects destination parents writable by other users", { skip: typeof process.getuid !== "function" }, () => {
  const parent = tempDir();
  fs.chmodSync(parent, 0o777);
  try {
    assert.throws(() => createRunDirectory(parent), { kind: "invalid-input" });
    assert.deepEqual(fs.readdirSync(parent), []);
  } finally {
    fs.chmodSync(parent, 0o700);
  }
});

test("rejects an unsafe ancestor before creating missing parents", { skip: typeof process.getuid !== "function" }, () => {
  const parent = tempDir();
  fs.chmodSync(parent, 0o777);
  try {
    assert.throws(() => createRunDirectory(path.join(parent, "missing", "child")), { kind: "invalid-input" });
    assert.deepEqual(fs.readdirSync(parent), []);
  } finally {
    fs.chmodSync(parent, 0o700);
  }
});

test("does not fetch more archives after consuming the shared byte budget", async () => {
  const zip = skillZip("budget", "name: budget");
  const entries = Array.from({ length: 5 }, (_, i) => entry(String(i), String(i)));
  const { fetchImpl, calls } = catalogFetch(entries, Object.fromEntries(entries.map((_, i) => [String(i), zip])));
  const result = await listSkills({
    ...ctxFor(fetchImpl), concurrency: 1, maxTotalBytes: zip.length * 2,
  }, { destRoot: tempDir() });

  assert.equal(result.downloaded, 2);
  assert.equal(result.failed.length, 3);
  assert.ok(result.failed.every((f) => f.errorKind === "too-large"));
  assert.equal(calls.filter((c) => !new URL(c.url).pathname.endsWith("/plugins")).length, 2);
});

test("aborts in-flight downloads and skips pending requests when a stream exceeds the shared budget", async () => {
  const entries = [entry("large", "large"), entry("waiting", "waiting"), entry("pending", "pending")];
  const calls = [];
  let abortReason;
  const fetchImpl = async (url, init) => {
    const file = new URL(url).pathname.split("/").pop();
    if (file === "plugins") return new Response(JSON.stringify({ entries }));
    calls.push(file);
    if (file === "large") {
      return new Response(new ReadableStream({
        start(controller) {
          controller.enqueue(Buffer.alloc(65));
          controller.close();
        },
      }));
    }
    if (file === "pending") assert.fail("an exhausted budget must not start another request");
    return new Promise((resolve, reject) => {
      const aborted = () => {
        abortReason = init.signal.reason;
        reject(abortReason);
      };
      if (init.signal.aborted) aborted();
      else init.signal.addEventListener("abort", aborted, { once: true });
    });
  };
  const result = await listSkills({
    ...ctxFor(fetchImpl), maxTotalBytes: 64, timeoutMs: 1000,
  }, { destRoot: tempDir() });

  assert.deepEqual(calls, ["large", "waiting"]);
  assert.equal(abortReason.kind, "too-large");
  assert.equal(result.downloaded, 0);
  assert.equal(result.failed.length, 3);
  assert.ok(result.failed.every((f) => f.errorKind === "too-large"));
});

test("enforces aggregate extracted-byte, file and directory budgets before writing an over-budget archive", async () => {
  const manifest = "---\nname: bounded\n---\n";
  const zip = makeZip([
    { name: "bounded/SKILL.md", data: manifest },
    { name: "bounded/assets/a.txt", data: "a".repeat(64) },
  ]);
  for (const limits of [
    { maxTotalExtractedBytes: Buffer.byteLength(manifest) + 64 },
    { maxTotalExtractedFiles: 2 },
    { maxTotalExtractedDirectories: 3 },
  ]) {
    const dest = tempDir();
    const { fetchImpl, calls } = catalogFetch(
      [entry("first", "first"), entry("second", "second"), entry("third", "third")],
      { first: zip, second: zip, third: zip }
    );
    const result = await listSkills({ ...ctxFor(fetchImpl), concurrency: 1, ...limits }, { destRoot: dest });
    assert.equal(result.downloaded, 1);
    assert.equal(result.failed.length, 2);
    assert.ok(result.failed.every((f) => f.errorKind === "too-large"));
    assert.equal(calls.filter((c) => !new URL(c.url).pathname.endsWith("/plugins")).length, 2);
    assert.equal(fs.readdirSync(dest).length, 1, "failed archives leave no container behind");
  }
});

test("rejects deeply nested bundles even when their leaf files fit the shared file budget", async () => {
  const extra = Array.from({ length: 3 }, (_, i) => ({
    name: `skills/demo/branch-${i}/${"d/".repeat(40)}payload.txt`,
    data: "x",
  }));
  const dest = tempDir();
  const { fetchImpl, calls } = catalogFetch(
    [entry("deep", "deep"), entry("pending", "pending")],
    { deep: skillZip("demo", "name: demo", extra) }
  );
  const result = await listSkills({
    ...ctxFor(fetchImpl), concurrency: 1, maxTotalExtractedFiles: 4, maxTotalExtractedDirectories: 10,
  }, { destRoot: dest });
  assert.equal(result.downloaded, 0);
  assert.equal(result.failed.length, 2);
  assert.ok(result.failed.every((f) => f.errorKind === "too-large"));
  assert.equal(calls.filter((c) => !new URL(c.url).pathname.endsWith("/plugins")).length, 1);
  assert.deepEqual(fs.readdirSync(dest), []);
});

test("classifies streamed and fallback body failures as network errors with their request IDs", async () => {
  for (const streamed of [true, false]) {
    for (const error of [
      new DOMException("fixture body timeout", "TimeoutError"),
      new DOMException("fixture body abort", "AbortError"),
      new TypeError("fixture body disconnect"),
    ]) {
      await assert.rejects(searchSkills(ctxFor(async () => failingBody(error, streamed))), {
        kind: "network", requestId: "req-body",
      });
      const { fetchImpl } = catalogFetch([entry("broken", "broken")], {
        broken: () => failingBody(error, streamed),
      });
      const result = await listSkills(ctxFor(fetchImpl), { destRoot: tempDir() });
      assert.equal(result.downloaded, 0);
      assert.equal(result.failed.length, 1);
      assert.equal(result.failed[0].errorKind, "network");
      assert.equal(result.failed[0].requestId, "req-body");
      assert.ok(result.failed[0].error.includes(error.message));
    }
  }
});

test("preserves existing WorkIqError instances raised while reading a response body", async () => {
  for (const streamed of [true, false]) {
    const error = new WorkIqError("fixture known failure", { kind: "too-large", requestId: "original-request" });
    await assert.rejects(searchSkills(ctxFor(async () => failingBody(error, streamed))), (e) => e === error);
    await assert.rejects(downloadEntry(
      ctxFor(async () => failingBody(error, streamed)), entry("known", "known"), tempDir()
    ), (e) => e === error);
  }
});

test("keeps malformed JSON classified as bad-response rather than network", async () => {
  await assert.rejects(searchSkills(ctxFor(async () => new Response("{not-json}", {
    headers: { "x-workiq-request-id": "req-json" },
  }))), (e) => e.kind === "bad-response" && e.requestId === "req-json" && /not JSON/.test(e.message));
});

test("sanitizes Windows reserved acquisition names without asserting importability", async () => {
  for (const name of ["CON", "nul", "COM1.txt", "LPT9", "aux.md"]) {
    assert.equal(sanitizeFolderName(name), `skill-${name}`);
  }
  assert.equal(sanitizeFolderName("CON." + "a".repeat(64)), "skill-CON." + "a".repeat(50));
  assert.equal(sanitizeFolderName("../safe/path"), "safe-path");
  assert.equal(sanitizeFolderName(""), "workiq-skill");

  const { fetchImpl } = catalogFetch([entry("reserved", "reserved")], {
    reserved: skillZip("payload", "name: CON"),
  });
  const result = await listSkills(ctxFor(fetchImpl), { destRoot: tempDir() });
  assert.deepEqual(result.failed, []);
  assert.equal(path.basename(result.skills[0].dir), "skill-CON");
  assert.ok(fs.existsSync(path.join(result.skills[0].dir, "SKILL.md")));
});

test("a bad entry fails on its own and the token never leaves WorkIQ", async () => {
  const dest = tempDir();
  const { fetchImpl, calls } = catalogFetch(
    [
      entry("good", "good"),
      entry("evil", "evil", { url: "https://attacker.example/steal.zip" }),
      entry("none", "none"),
      entry("two", "two"),
    ],
    {
      good: skillZip("good-skill", "name: good-skill\ndescription: ok"),
      none: makeZip([{ name: "README.md", data: "no manifest" }]),
      two: makeZip([
        { name: "skills/a/SKILL.md", data: "---\nname: a\n---\n" },
        { name: "skills/b/SKILL.md", data: "---\nname: b\n---\n" },
      ]),
    }
  );

  const result = await listSkills(ctxFor(fetchImpl), { destRoot: dest });

  assert.deepEqual(result.skills.map((s) => s.name), ["good-skill"]);
  const kinds = Object.fromEntries(result.failed.map((f) => [f.identifier.split(":").pop(), f.errorKind]));
  assert.deepEqual(kinds, { evil: "untrusted-url", none: "bad-bundle", two: "bad-bundle" });
  assert.ok(!calls.some((c) => c.url.startsWith("https://attacker.example")));
});

test("follows a download redirect but drops the token once it leaves WorkIQ", async () => {
  const dest = tempDir();
  const zip = skillZip("moved", "name: moved\ndescription: d");
  const { fetchImpl, calls } = catalogFetch([entry("m", "moved")], {
    moved: () => new Response(null, { status: 302, headers: { location: "https://storage.example/blob/moved" } }),
  });
  const wrapped = async (url, init) => {
    if (url.startsWith("https://storage.example/")) {
      calls.push({ url, headers: init.headers });
      return new Response(zip, { status: 200 });
    }
    return fetchImpl(url, init);
  };

  const result = await listSkills(ctxFor(wrapped), { destRoot: dest });

  assert.deepEqual(result.skills.map((s) => s.name), ["moved"]);
  const download = calls.find((c) => c.url.includes("/artifacts/moved"));
  const blob = calls.find((c) => c.url.startsWith("https://storage.example/"));
  assert.equal(download.redirect, "manual");
  assert.equal(download.headers.Authorization, "Bearer token-for-tests");
  assert.equal(blob.headers.Authorization, undefined);
});

test("retries a 502 once before reporting WorkIQ unavailable", async () => {
  let attempts = 0;
  const flaky = async () => {
    attempts++;
    return attempts === 1
      ? new Response("", { status: 502 })
      : new Response(JSON.stringify({ entries: [] }), { status: 200 });
  };
  const ok = await searchSkills(ctxFor(flaky));
  assert.equal(attempts, 2);
  assert.equal(ok.available, 0);

  attempts = 0;
  const down = async () => {
    attempts++;
    return new Response("", { status: 502, headers: { "x-workiq-request-id": "req-502" } });
  };
  await assert.rejects(searchSkills(ctxFor(down)), { kind: "unavailable", status: 502, requestId: "req-502" });
  assert.equal(attempts, 2);
});

test("a search 404 means the catalog is not enabled and keeps the request ID", async () => {
  const { fetchImpl } = catalogFetch([], {}, { searchStatus: 404, searchHeaders: { "x-workiq-request-id": "req-404" } });
  await assert.rejects(listSkills(ctxFor(fetchImpl), { destRoot: tempDir() }), {
    kind: "not-enabled",
    status: 404,
    requestId: "req-404",
  });
});

// Simulates the browser: follows the authorize URL's redirect_uri back to the loopback server.
function browserReturning(params) {
  return (authorizeUrl) => {
    const u = new URL(authorizeUrl);
    const back = new URL(u.searchParams.get("redirect_uri").replace("localhost", "127.0.0.1"));
    const values = typeof params === "function" ? params(u.searchParams) : params;
    for (const [k, v] of Object.entries(values)) back.searchParams.set(k, v);
    fetch(back).catch(() => {});
    return true;
  };
}

test("browser sign-in exchanges the code with a matching PKCE verifier", async () => {
  let authorize;
  let tokenRequest;
  const token = await signIn(
    { clientId: CLIENT_ID, tenantId: "organizations", timeoutMs: 10000 },
    {
      openBrowser: (url) => {
        authorize = new URL(url);
        return browserReturning((p) => ({ code: "the-code", state: p.get("state") }))(url);
      },
      fetchImpl: async (url, init) => {
        tokenRequest = { url, form: new URLSearchParams(init.body) };
        return new Response(JSON.stringify({ access_token: "issued-token" }), { status: 200 });
      },
    }
  );

  assert.equal(token, "issued-token");
  assert.equal(authorize.searchParams.get("code_challenge_method"), "S256");
  assert.equal(authorize.searchParams.get("scope"), SCOPE);
  assert.equal(tokenRequest.url, "https://login.microsoftonline.com/organizations/oauth2/v2.0/token");
  const form = tokenRequest.form;
  assert.equal(form.get("code"), "the-code");
  assert.equal(form.get("client_id"), CLIENT_ID);
  assert.equal(form.get("redirect_uri"), authorize.searchParams.get("redirect_uri"));
  const challenge = crypto.createHash("sha256").update(form.get("code_verifier")).digest("base64url");
  assert.equal(challenge, authorize.searchParams.get("code_challenge"));
});

test("an invalid-state callback does not terminate the pending legitimate sign-in", async () => {
  const loopback = await startLoopback({ state: "legitimate-state", timeoutMs: 10000 });
  let settled = false;
  loopback.result.then(() => { settled = true; }, () => { settled = true; });
  try {
    const callback = new URL(loopback.redirectUri.replace("localhost", "127.0.0.1"));
    callback.searchParams.set("code", "unsolicited");
    callback.searchParams.set("state", "wrong-state");
    const invalid = await fetch(callback);
    assert.equal(invalid.status, 400);
    await invalid.text();
    assert.equal(settled, false);

    callback.searchParams.set("code", "legitimate-code");
    callback.searchParams.set("state", "legitimate-state");
    const valid = await fetch(callback);
    assert.equal(valid.status, 200);
    await valid.text();
    assert.deepEqual(await loopback.result, { code: "legitimate-code" });
  } finally {
    loopback.close();
  }
});

test("sign-in fails cleanly on a cancel, a policy block, or a timeout", async () => {
  const noToken = async () => assert.fail("no token request expected");
  const run = (openBrowser, timeoutMs = 10000) =>
    signIn({ clientId: CLIENT_ID, tenantId: "organizations", timeoutMs }, { openBrowser, fetchImpl: noToken });

  await assert.rejects(
    run(browserReturning((p) => ({ error: "access_denied", error_description: "AADSTS65004: User declined.", state: p.get("state") }))),
    { kind: "sign-in-cancelled" }
  );
  await assert.rejects(
    run(browserReturning((p) => ({ error: "access_denied", error_description: "AADSTS53003: Blocked by Conditional Access.", state: p.get("state") }))),
    (e) => e.kind === "auth" && /Conditional Access/.test(e.message)
  );
  await assert.rejects(run(() => true, 100), { kind: "sign-in-timeout" });
});
