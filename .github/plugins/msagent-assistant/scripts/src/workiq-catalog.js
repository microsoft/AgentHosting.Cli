/**
 * workiq-catalog.js — list the Agent Skills the signed-in user can use from the WorkIQ AI Catalog.
 *
 * The WorkIQ AI Catalog serves agentic resources (plugins, skills, MCP server cards) per
 * Microsoft 365 user. This helper reads only skill bundles (application/agent-skills+zip):
 *
 *   GET <base>/api/catalogs/agenticResources/plugins?type=application/agent-skills+zip
 *       -> { entries: [{ identifier, displayName, description, type, url, version }] }
 *   GET <entry.url>   (exactly as returned, on the same WorkIQ host) -> the skill ZIP
 *
 * A skill entry's displayName/description describe the plugin that ships it, so every bundle is
 * downloaded and its SKILL.md frontmatter supplies the skill's own name and description. Each
 * skill is unpacked into a private, unique folder with SKILL.md at the top, ready to pass to
 * `add-skill.bundle.js import --src`.
 *
 * Sign-in uses Entra ID authorization code + PKCE through the system browser and a loopback
 * redirect (device code sign-in is commonly blocked by Conditional Access). The token is kept in
 * memory for this run only; it is never cached, written or printed.
 *
 * Usage:
 *   node workiq-catalog.bundle.js list [--query "<text>"] [--dest <dir>] [--client-id <guid>]
 *        [--tenant-id <tenant>] [--timeout <seconds>] [--no-browser] [--dry-run]
 *
 * Output (stdout): one JSON object, { ok: true, ... } or { ok: false, errorKind, error, ... }.
 * The sign-in URL and progress messages go to stderr.
 */

const crypto = require("crypto");
const fs = require("fs");
const http = require("http");
const os = require("os");
const path = require("path");
const { spawn } = require("child_process");
const yaml = require("js-yaml");
const { extractZip, isReservedDeviceName } = require("./zip-extract");

const BASE_URL = "https://workiq.svc.cloud.microsoft";
const SEARCH_PATH = "/api/catalogs/agenticResources/plugins";
const SKILL_TYPE = "application/agent-skills+zip";
const AUTHORITY_HOST = "login.microsoftonline.com";
// WorkIQ's public client app, provisioned in the Microsoft tenant.
const DEFAULT_CLIENT_ID = "ba081686-5d24-4bc6-a0d6-d034ecffed87";
const DEFAULT_TENANT = "organizations";
const SCOPE = "api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask openid profile";
const SOURCE = Object.freeze({ source: "workiq", sourceName: "WorkIQ AI Catalog" });

const DEFAULT_SIGN_IN_TIMEOUT_S = 180;
const REQUEST_TIMEOUT_MS = 30000;
const RETRY_DELAY_MS = 2000;
const MAX_REDIRECTS = 3;
const MAX_ENTRIES = 100;
const MAX_JSON_BYTES = 5 * 1024 * 1024;
const MAX_ZIP_BYTES = 10 * 1024 * 1024;
const MAX_TOTAL_BYTES = 100 * 1024 * 1024;
const MAX_EXTRACTED_BYTES = 20 * 1024 * 1024;
const MAX_TOTAL_EXTRACTED_BYTES = 100 * 1024 * 1024;
const MAX_TOTAL_EXTRACTED_FILES = 10000;
const MAX_TOTAL_EXTRACTED_DIRECTORIES = 10000;
const DOWNLOAD_CONCURRENCY = 4;

const GUID_RE = /^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$/;
const TENANT_RE = /^(?:organizations|common|[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}|[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+)$/;
const CONTROL_RE = /[\u0000-\u001f\u007f]/;
// Control, line/paragraph separator, zero-width and bidi-override characters are stripped from
// catalog text before it is shown, so untrusted names cannot reorder or hide what the user sees.
const UNSAFE_DISPLAY_RE = /[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u2028-\u202e\u2066-\u2069\ufeff]+/g;
const REDIRECT_STATUSES = new Set([301, 302, 303, 307, 308]);

class WorkIqError extends Error {
  constructor(message, { kind, status, requestId } = {}) {
    super(message);
    this.kind = kind || "error";
    if (status !== undefined) this.status = status;
    if (requestId) this.requestId = requestId;
  }
}

function log(msg) {
  process.stderr.write(msg + "\n");
}

function inputError(message) {
  return new WorkIqError(message, { kind: "invalid-input" });
}

function errorMessage(e) {
  return e && e.message ? e.message : String(e);
}

function cleanText(value, max) {
  if (typeof value !== "string") return "";
  const s = value.replace(UNSAFE_DISPLAY_RE, " ").replace(/\s+/g, " ").trim();
  return s.length > max ? s.slice(0, max - 1).trimEnd() + "\u2026" : s;
}

function firstLine(s) {
  return String(s || "").split(/\r?\n/)[0].trim();
}

// ---------------------------------------------------------------------------
// Sign-in: authorization code + PKCE with a loopback redirect.
// ---------------------------------------------------------------------------

const AUTH_HINTS = [
  [/AADSTS53003\b/, "Your organization's Conditional Access policy blocked this sign-in. Sign in with the work account and device your organization allows for WorkIQ."],
  [/AADSTS50059\b/, "Microsoft could not tell which tenant to use. Pass --tenant-id with your tenant ID or domain."],
  [/AADSTS50020\b/, "That account is not in the tenant you signed in to. Sign in with your work account, or pass --tenant-id."],
  [/AADSTS700016\b|unauthorized_client/, "The sign-in app (--client-id) is not available in your tenant. Ask your WorkIQ administrator which client ID is enabled for your tenant."],
  [/AADSTS7000218\b|invalid_client/, "The sign-in app (--client-id) does not allow public client sign-in."],
  [/AADSTS65001\b|consent_required/, "Consent for the WorkIQ permission is missing. An administrator may need to grant it for your tenant."],
  [/AADSTS500011\b|AADSTS70011\b|invalid_scope/, "The WorkIQ permission is not available to this sign-in app in your tenant."],
  [/AADSTS50011\b/, "The sign-in app does not allow the http://localhost redirect this helper uses."],
  [/invalid_grant/, "The sign-in code expired or was already used. Run the command again."],
];

function authHint(text) {
  const s = String(text || "");
  const hit = AUTH_HINTS.find(([re]) => re.test(s));
  return hit ? hit[1] : "";
}

function authError(code, description) {
  const detail = cleanText(firstLine(description), 300);
  const hint = authHint(`${code || ""} ${description || ""}`);
  const label = cleanText(code, 80);
  return new WorkIqError(
    `WorkIQ sign-in failed${label ? `: ${label}` : ""}${detail ? ` (${detail})` : ""}.${hint ? " " + hint : ""}`,
    { kind: "auth" }
  );
}

function authorizeError(code, description) {
  if (code === "access_denied" && !authHint(description)) {
    return new WorkIqError("Sign-in was cancelled in the browser.", { kind: "sign-in-cancelled" });
  }
  return authError(code, description);
}

function base64url(buf) {
  return buf.toString("base64").replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function createPkce() {
  const verifier = base64url(crypto.randomBytes(32));
  const challenge = base64url(crypto.createHash("sha256").update(verifier).digest());
  const state = base64url(crypto.randomBytes(16));
  return { verifier, challenge, state };
}

function authorityBase(tenantId) {
  return `https://${AUTHORITY_HOST}/${encodeURIComponent(tenantId)}/oauth2/v2.0`;
}

function buildAuthorizeUrl({ clientId, tenantId, redirectUri, challenge, state }) {
  const u = new URL(`${authorityBase(tenantId)}/authorize`);
  u.searchParams.set("client_id", clientId);
  u.searchParams.set("response_type", "code");
  u.searchParams.set("redirect_uri", redirectUri);
  u.searchParams.set("response_mode", "query");
  u.searchParams.set("scope", SCOPE);
  u.searchParams.set("code_challenge", challenge);
  u.searchParams.set("code_challenge_method", "S256");
  u.searchParams.set("state", state);
  u.searchParams.set("prompt", "select_account");
  return u.toString();
}

// Static text only: nothing from the request is reflected into the page.
function reply(res, status, message) {
  res.writeHead(status, {
    "Content-Type": "text/html; charset=utf-8",
    "Cache-Control": "no-store",
    "Content-Security-Policy": "default-src 'none'",
    "Referrer-Policy": "no-referrer",
    Connection: "close",
  });
  res.end(`<!doctype html><meta charset="utf-8"><title>WorkIQ sign-in</title><p>${message}</p>`);
}

function listen(server, port, host) {
  return new Promise((resolve, reject) => {
    const onError = (e) => {
      server.removeListener("listening", onListening);
      reject(e);
    };
    const onListening = () => {
      server.removeListener("error", onError);
      resolve();
    };
    server.once("error", onError);
    server.once("listening", onListening);
    server.listen(port, host);
  });
}

// Waits for the browser redirect on http://localhost:<port>. `result` resolves with { code } or
// rejects with a WorkIqError; call close() when done.
async function startLoopback({ state, timeoutMs }) {
  let timer = null;
  let settled = false;
  let resolveResult;
  let rejectResult;
  const result = new Promise((resolve, reject) => {
    resolveResult = resolve;
    rejectResult = reject;
  });
  result.catch(() => {});
  const settle = (err, value) => {
    if (settled) return;
    settled = true;
    clearTimeout(timer);
    if (err) rejectResult(err);
    else resolveResult(value);
  };

  const handler = (req, res) => {
    let params;
    try {
      params = new URL(req.url, "http://localhost").searchParams;
    } catch {
      params = new URLSearchParams();
    }
    if (req.method !== "GET" || (!params.has("code") && !params.has("error"))) {
      reply(res, 404, "Not found.");
      return;
    }
    if (settled) {
      reply(res, 200, "This sign-in has already finished. Return to the terminal.");
      return;
    }
    if (params.get("state") !== state) {
      reply(res, 400, "Sign-in failed. Return to the terminal.");
      return;
    }
    if (params.has("error")) {
      reply(res, 200, "Sign-in did not complete. Return to the terminal.");
      settle(authorizeError(params.get("error"), params.get("error_description")));
      return;
    }
    reply(res, 200, "Sign-in complete. You can close this tab.");
    settle(null, { code: params.get("code") });
  };

  const servers = [];
  const primary = http.createServer(handler);
  await listen(primary, 0, "127.0.0.1");
  servers.push(primary);
  const port = primary.address().port;
  // Browsers may resolve localhost to ::1, so listen there on the same port when possible.
  const secondary = http.createServer(handler);
  try {
    await listen(secondary, port, "::1");
    servers.push(secondary);
  } catch {
    // IPv6 loopback unavailable; 127.0.0.1 is enough.
  }

  timer = setTimeout(() => {
    settle(new WorkIqError(
      `Sign-in did not finish within ${Math.round(timeoutMs / 1000)} seconds. Run the command again and complete the browser sign-in.`,
      { kind: "sign-in-timeout" }
    ));
  }, timeoutMs);

  const close = () => {
    clearTimeout(timer);
    for (const server of servers) {
      server.close();
      if (typeof server.closeIdleConnections === "function") server.closeIdleConnections();
      // Let the final page flush before dropping any connection the browser kept open.
      if (typeof server.closeAllConnections === "function") {
        setTimeout(() => server.closeAllConnections(), 1000).unref();
      }
    }
  };

  return { redirectUri: `http://localhost:${port}`, result, close };
}

// Opens the URL without a shell, so nothing in it is interpreted as a command.
function openBrowser(url, { spawnImpl = spawn, platform = process.platform } = {}) {
  let command;
  let args;
  if (platform === "win32") {
    command = path.join(process.env.SystemRoot || "C:\\Windows", "System32", "rundll32.exe");
    args = ["url.dll,FileProtocolHandler", url];
  } else if (platform === "darwin") {
    command = "open";
    args = [url];
  } else {
    command = "xdg-open";
    args = [url];
  }
  try {
    const child = spawnImpl(command, args, { detached: true, stdio: "ignore", windowsHide: true });
    if (child && typeof child.on === "function") child.on("error", () => {});
    if (child && typeof child.unref === "function") child.unref();
    return true;
  } catch {
    return false;
  }
}

async function exchangeCode({ clientId, tenantId, code, redirectUri, verifier }, fetchImpl = fetch) {
  const body = new URLSearchParams({
    client_id: clientId,
    grant_type: "authorization_code",
    code,
    redirect_uri: redirectUri,
    code_verifier: verifier,
    scope: SCOPE,
  });
  let res;
  try {
    res = await fetchImpl(`${authorityBase(tenantId)}/token`, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" },
      body: body.toString(),
      redirect: "error",
      signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    });
  } catch (e) {
    throw new WorkIqError(`Could not reach Microsoft sign-in: ${errorMessage(e)}`, { kind: "network" });
  }
  let json = null;
  try {
    json = await res.json();
  } catch {
    // reported below
  }
  if (!res.ok || !json || typeof json.access_token !== "string" || !json.access_token) {
    throw authError(json && json.error ? json.error : `HTTP ${res.status}`, json && json.error_description);
  }
  return json.access_token;
}

async function signIn({ clientId, tenantId, timeoutMs, noBrowser }, deps = {}) {
  const pkce = (deps.createPkce || createPkce)();
  const loopback = await startLoopback({ state: pkce.state, timeoutMs });
  try {
    const url = buildAuthorizeUrl({
      clientId, tenantId, redirectUri: loopback.redirectUri, challenge: pkce.challenge, state: pkce.state,
    });
    const opened = !noBrowser && (deps.openBrowser || openBrowser)(url);
    log(opened
      ? "Opening your browser to sign in to WorkIQ. If it does not open, visit this URL:"
      : "Sign in to WorkIQ by opening this URL in your browser:");
    log(url);
    const { code } = await loopback.result;
    log("Signed in.");
    return await exchangeCode(
      { clientId, tenantId, code, redirectUri: loopback.redirectUri, verifier: pkce.verifier },
      deps.fetchImpl || fetch
    );
  } finally {
    loopback.close();
  }
}

// ---------------------------------------------------------------------------
// WorkIQ HTTP
// ---------------------------------------------------------------------------

const OPERATION_LABEL = {
  search: "listing your WorkIQ skills",
  download: "downloading a skill bundle",
};

function httpError(status, op, requestId) {
  const doing = OPERATION_LABEL[op] || "calling WorkIQ";
  const make = (kind, message) => new WorkIqError(message, { kind, status, requestId });
  switch (status) {
    case 400:
      return make("bad-request", `WorkIQ rejected the request (400) while ${doing}.`);
    case 401:
      return make("unauthorized", `WorkIQ did not accept the sign-in token (401) while ${doing}. Run the command again to sign in.`);
    case 403:
      return make("forbidden", `WorkIQ refused access (403) while ${doing}. Your account or this sign-in app is not allowed to use the WorkIQ AI Catalog.`);
    case 404:
      return op === "download"
        ? make("not-found", `WorkIQ returned 404 while ${doing}. The skill may no longer be available to you; list again.`)
        : make("not-enabled", `WorkIQ returned 404 while ${doing}. The WorkIQ AI Catalog is not enabled for your tenant or for this sign-in app (client ID).`);
    case 413:
      return make("too-large", `The skill bundle is larger than the ${MAX_ZIP_BYTES / (1024 * 1024)} MB limit.`);
    case 502:
    case 503:
      return make("unavailable", `WorkIQ is temporarily unavailable (${status}) while ${doing}. Try again later.`);
    default:
      return make("http", `WorkIQ returned HTTP ${status} while ${doing}.`);
  }
}

// The token may only ever be sent to the WorkIQ host over HTTPS.
function assertWorkIqUrl(url, baseUrl = BASE_URL) {
  let parsed;
  try {
    parsed = new URL(url);
  } catch {
    throw new WorkIqError("WorkIQ returned an invalid URL.", { kind: "bad-response" });
  }
  const trusted = new URL(baseUrl).origin;
  if (parsed.protocol !== "https:" || parsed.origin !== trusted) {
    throw new WorkIqError(`Refusing to send the WorkIQ token to ${parsed.origin}; only ${trusted} is trusted.`, { kind: "untrusted-url" });
  }
  return parsed;
}

function header(res, name) {
  return res && res.headers && typeof res.headers.get === "function" ? res.headers.get(name) : null;
}

function discard(res) {
  try {
    if (res && res.body && typeof res.body.cancel === "function") res.body.cancel().catch(() => {});
  } catch {
    // ignore
  }
}

function createDownloadBudget(ctx) {
  return {
    maxBytes: ctx.maxTotalBytes ?? MAX_TOTAL_BYTES,
    maxExtractedBytes: ctx.maxTotalExtractedBytes ?? MAX_TOTAL_EXTRACTED_BYTES,
    maxFiles: ctx.maxTotalExtractedFiles ?? MAX_TOTAL_EXTRACTED_FILES,
    maxDirectories: ctx.maxTotalExtractedDirectories ?? MAX_TOTAL_EXTRACTED_DIRECTORIES,
    downloadedBytes: 0,
    extractedBytes: 0,
    extractedFiles: 0,
    extractedDirectories: 0,
    controller: new AbortController(),
    error: null,
  };
}

function exhaustBudget(budget, message) {
  if (!budget.error) {
    budget.error = new WorkIqError(message, { kind: "too-large" });
    budget.controller.abort(budget.error);
  }
  return budget.error;
}

// GET with the token on the WorkIQ origin only. Redirects are followed manually so the token is
// dropped if a redirect leaves that origin. A 502/503 is retried once.
async function request(ctx, url, { op, accept }) {
  const baseUrl = ctx.baseUrl || BASE_URL;
  const trusted = new URL(baseUrl).origin;
  const fetchImpl = ctx.fetchImpl || fetch;
  const sleep = ctx.sleep || ((ms) => new Promise((resolve) => setTimeout(resolve, ms)));
  let target = assertWorkIqUrl(url, baseUrl);
  let redirects = 0;
  let retried = false;
  for (;;) {
    if (ctx.budget && ctx.budget.error) throw ctx.budget.error;
    const headers = { Accept: accept };
    if (target.origin === trusted) headers.Authorization = `Bearer ${ctx.token}`;
    let res;
    try {
      res = await fetchImpl(target.toString(), {
        method: "GET",
        headers,
        redirect: "manual",
        signal: ctx.budget
          ? AbortSignal.any([AbortSignal.timeout(ctx.timeoutMs || REQUEST_TIMEOUT_MS), ctx.budget.controller.signal])
          : AbortSignal.timeout(ctx.timeoutMs || REQUEST_TIMEOUT_MS),
      });
    } catch (e) {
      if (ctx.budget && ctx.budget.error) throw ctx.budget.error;
      throw new WorkIqError(`Could not reach WorkIQ while ${OPERATION_LABEL[op] || "calling WorkIQ"}: ${errorMessage(e)}`, { kind: "network" });
    }
    const requestId = header(res, "x-workiq-request-id") || undefined;
    if (REDIRECT_STATUSES.has(res.status)) {
      discard(res);
      if (++redirects > MAX_REDIRECTS) {
        throw new WorkIqError("WorkIQ redirected too many times.", { kind: "bad-response", requestId });
      }
      let next;
      try {
        next = new URL(header(res, "location"), target);
      } catch {
        throw new WorkIqError("WorkIQ returned a redirect without a valid location.", { kind: "bad-response", requestId });
      }
      if (next.protocol !== "https:") {
        throw new WorkIqError(`Refusing to follow a redirect to ${next.origin}; only HTTPS is allowed.`, { kind: "untrusted-url", requestId });
      }
      target = next;
      continue;
    }
    if ((res.status === 502 || res.status === 503) && !retried) {
      discard(res);
      retried = true;
      await sleep(ctx.retryDelayMs === undefined ? RETRY_DELAY_MS : ctx.retryDelayMs);
      continue;
    }
    if (!res.ok) {
      discard(res);
      throw httpError(res.status, op, requestId);
    }
    return res;
  }
}

async function readLimited(res, max, tooLarge, budget) {
  const declared = Number(header(res, "content-length"));
  if (Number.isFinite(declared) && declared > max) {
    discard(res);
    throw tooLarge();
  }
  if (budget && Number.isFinite(declared) && declared > budget.maxBytes - budget.downloadedBytes) {
    discard(res);
    throw exhaustBudget(budget, "The skill bundles together exceed the download limit for one run.");
  }
  const account = (length) => {
    if (!budget) return;
    if (budget.error) throw budget.error;
    budget.downloadedBytes += length;
    if (budget.downloadedBytes > budget.maxBytes) {
      throw exhaustBudget(budget, "The skill bundles together exceed the download limit for one run.");
    }
  };
  try {
    if (res.body && typeof res.body[Symbol.asyncIterator] === "function") {
      const chunks = [];
      let total = 0;
      for await (const chunk of res.body) {
        account(chunk.length);
        total += chunk.length;
        if (total > max) {
          discard(res);
          throw tooLarge();
        }
        chunks.push(Buffer.from(chunk));
      }
      return Buffer.concat(chunks);
    }
    const buf = Buffer.from(await res.arrayBuffer());
    account(buf.length);
    if (buf.length > max) throw tooLarge();
    return buf;
  } catch (e) {
    discard(res);
    if (budget && budget.error) throw budget.error;
    if (e instanceof WorkIqError) throw e;
    throw new WorkIqError(`Could not read the WorkIQ response: ${errorMessage(e)}`, {
      kind: "network",
      requestId: header(res, "x-workiq-request-id") || undefined,
    });
  }
}

// ---------------------------------------------------------------------------
// Catalog
// ---------------------------------------------------------------------------

// Keep downloadable skill bundles only; ignore other resource types and malformed entries.
function normalizeEntries(raw) {
  const out = [];
  const seen = new Set();
  for (const e of Array.isArray(raw) ? raw : []) {
    if (!e || typeof e !== "object" || e.type !== SKILL_TYPE) continue;
    if (typeof e.identifier !== "string" || !e.identifier || e.identifier.length > 1024 || CONTROL_RE.test(e.identifier)) continue;
    if (typeof e.url !== "string" || !e.url) continue;
    if (seen.has(e.identifier)) continue;
    seen.add(e.identifier);
    out.push({
      identifier: e.identifier,
      url: e.url,
      plugin: {
        name: cleanText(e.displayName, 128) || null,
        version: cleanText(e.version, 64) || null,
        description: cleanText(e.description, 1024),
      },
    });
  }
  return out;
}

async function searchSkills(ctx) {
  const url = new URL(SEARCH_PATH, ctx.baseUrl || BASE_URL);
  url.searchParams.set("type", SKILL_TYPE);
  const res = await request(ctx, url.toString(), { op: "search", accept: "application/json" });
  const requestId = header(res, "x-workiq-request-id") || undefined;
  const bad = (why) => new WorkIqError(`WorkIQ returned an unexpected catalog response (${why}).`, { kind: "bad-response", requestId });
  let json;
  try {
    json = JSON.parse((await readLimited(res, MAX_JSON_BYTES, () => bad("too large"))).toString("utf8"));
  } catch (e) {
    if (e instanceof WorkIqError) throw e;
    throw bad("not JSON");
  }
  const raw = json && (Array.isArray(json.entries) ? json.entries : Array.isArray(json.results) ? json.results : null);
  if (!raw) throw bad("no entries");
  const entries = normalizeEntries(raw);
  return {
    entries: entries.slice(0, MAX_ENTRIES),
    available: entries.length,
    truncated: entries.length > MAX_ENTRIES,
    requestId,
  };
}

function parseFrontmatter(text) {
  const s = String(text || "").replace(/^\uFEFF/, "");
  const m = /^---[ \t]*\r?\n([\s\S]*?)\r?\n---[ \t]*(?:\r?\n|$)/.exec(s);
  if (!m) return {};
  let doc;
  try {
    doc = yaml.load(m[1]);
  } catch {
    return {};
  }
  if (!doc || typeof doc !== "object" || Array.isArray(doc)) return {};
  return {
    name: typeof doc.name === "string" ? doc.name : undefined,
    description: typeof doc.description === "string" ? doc.description : undefined,
  };
}

// Acquisition cache names are not proof of portable manifest validity; import validates identity.
function sanitizeFolderName(name) {
  let s = String(name || "")
    .replace(/[^A-Za-z0-9._-]+/g, "-")
    .replace(/-+/g, "-")
    .replace(/^[-.]+|[-.]+$/g, "");
  if (isReservedDeviceName(s)) s = `skill-${s}`;
  if (s.length > 60) s = s.slice(0, 60).replace(/[-.]+$/g, "");
  return s || "workiq-skill";
}

function assertPrivateDirectory(dir) {
  const stat = fs.lstatSync(dir);
  if (stat.isSymbolicLink() || !stat.isDirectory()) {
    throw inputError("The download destination must be a real directory, not a symbolic link or junction.");
  }
  if (typeof process.getuid === "function" && (stat.uid !== process.getuid() || (stat.mode & 0o077) !== 0)) {
    throw inputError("The download directory must be owned by the current user and inaccessible to other users.");
  }
}

function createRunDirectory(parent) {
  let existing = path.resolve(parent);
  const missing = [];
  let stat;
  for (;;) {
    try {
      stat = fs.lstatSync(existing);
      break;
    } catch (e) {
      const next = path.dirname(existing);
      if (e.code !== "ENOENT" || next === existing) throw e;
      missing.unshift(path.basename(existing));
      existing = next;
    }
  }
  if ((!missing.length && stat.isSymbolicLink()) || (!stat.isDirectory() && !stat.isSymbolicLink())) {
    throw inputError("--dest must be a real directory, not a symbolic link or junction.");
  }
  let canonicalParent = fs.realpathSync(existing);
  if (!fs.statSync(canonicalParent).isDirectory()) {
    throw inputError("--dest must be a real directory, not a symbolic link or junction.");
  }
  if (typeof process.getuid === "function") {
    let ancestor = canonicalParent;
    for (;;) {
      const info = fs.statSync(ancestor);
      const owned = info.uid === process.getuid() || info.uid === 0;
      const sticky = (info.mode & 0o1000) !== 0;
      if (!owned || ((info.mode & 0o022) !== 0 && !sticky)) {
        throw inputError("--dest and its parent directories must not be controlled by other users.");
      }
      const next = path.dirname(ancestor);
      if (next === ancestor) break;
      ancestor = next;
    }
  }
  for (const segment of missing) {
    canonicalParent = path.join(canonicalParent, segment);
    try {
      fs.mkdirSync(canonicalParent, { mode: 0o700 });
    } catch (e) {
      if (e.code === "EEXIST") throw inputError("--dest changed while being created. Run again with a private destination.");
      throw e;
    }
  }
  const dir = fs.mkdtempSync(path.join(canonicalParent, "mcs-add-skill-workiq-"));
  assertPrivateDirectory(dir);
  return dir;
}

function isManifest(rel) {
  return rel.split("/").pop().toLowerCase() === "skill.md";
}

// Every container is exclusive to this download; no prior run's files are replaced or deleted.
async function downloadEntry(ctx, entry, destRoot) {
  assertPrivateDirectory(destRoot);
  const budget = ctx.budget || createDownloadBudget(ctx);
  if (budget.error) throw budget.error;
  if (budget.downloadedBytes >= budget.maxBytes) {
    throw exhaustBudget(budget, "The skill bundles together exceed the download limit for one run.");
  }
  ctx = { ...ctx, budget };
  const res = await request(ctx, entry.url, { op: "download", accept: "application/zip" });
  const requestId = header(res, "x-workiq-request-id") || undefined;
  const archive = await readLimited(res, MAX_ZIP_BYTES, () => httpError(413, "download", requestId), budget);

  const prefix = crypto.createHash("sha256").update(entry.identifier).digest("hex").slice(0, 12);
  const container = fs.mkdtempSync(path.join(destRoot, `${prefix}-`));
  const staging = path.join(container, ".download");
  let promoted = false;
  try {
    const unpacked = path.join(staging, "bundle");
    let rels;
    try {
      rels = extractZip(archive, unpacked, {
        maxTotalBytes: MAX_EXTRACTED_BYTES,
        beforeExtract: ({ totalBytes, fileCount, directoryCount }) => {
          if (budget.error) throw budget.error;
          if (totalBytes > budget.maxExtractedBytes - budget.extractedBytes
              || fileCount > budget.maxFiles - budget.extractedFiles
              || directoryCount > budget.maxDirectories - budget.extractedDirectories) {
            throw exhaustBudget(budget, "The skill bundles together exceed the extracted-byte, file or directory limit for one run.");
          }
          budget.extractedBytes += totalBytes;
          budget.extractedFiles += fileCount;
          budget.extractedDirectories += directoryCount;
        },
      });
    } catch (e) {
      if (e instanceof WorkIqError) throw e;
      throw new WorkIqError(`The skill bundle could not be unpacked: ${errorMessage(e)}`, { kind: "bad-bundle" });
    }
    const manifests = rels.filter(isManifest);
    if (manifests.length !== 1) {
      throw new WorkIqError(
        manifests.length
          ? `The skill bundle contains ${manifests.length} SKILL.md files; expected exactly one.`
          : "The skill bundle has no SKILL.md, so it is not an importable Agent Skill.",
        { kind: "bad-bundle" }
      );
    }
    const manifestRel = manifests[0];
    const cut = manifestRel.lastIndexOf("/");
    const skillRel = cut < 0 ? "" : manifestRel.slice(0, cut);
    const skillRoot = skillRel ? path.join(unpacked, ...skillRel.split("/")) : unpacked;
    const meta = parseFrontmatter(fs.readFileSync(path.join(unpacked, ...manifestRel.split("/")), "utf8"));
    const name = cleanText(meta.name, 64)
      || cleanText(skillRel ? skillRel.split("/").pop() : entry.plugin.name, 64)
      || "workiq-skill";
    const target = path.join(container, sanitizeFolderName(name));

    fs.renameSync(skillRoot, target);
    promoted = true;

    return {
      name,
      description: cleanText(meta.description, 1024) || entry.plugin.description,
      plugin: { name: entry.plugin.name, version: entry.plugin.version },
      identifier: entry.identifier,
      dir: target,
      fileCount: rels.filter((rel) => !skillRel || rel.startsWith(skillRel + "/")).length,
    };
  } finally {
    fs.rmSync(promoted ? staging : container, { recursive: true, force: true });
  }
}

function normalizeQuery(text) {
  return String(text || "").toLowerCase().replace(/[^\p{L}\p{N}]+/gu, " ").trim();
}

function filterSkills(skills, query) {
  const tokens = normalizeQuery(query).split(" ").filter(Boolean);
  if (!tokens.length) return skills;
  return skills.filter((s) => {
    const haystack = normalizeQuery(`${s.name} ${s.description} ${s.plugin && s.plugin.name ? s.plugin.name : ""}`);
    return tokens.every((t) => haystack.includes(t));
  });
}

async function mapLimit(items, limit, fn) {
  const out = new Array(items.length);
  let next = 0;
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (next < items.length) {
      const i = next++;
      out[i] = await fn(items[i], i);
    }
  });
  await Promise.all(workers);
  return out;
}

async function listSkills(ctx, { query, destRoot }) {
  const search = await searchSkills(ctx);
  ctx = { ...ctx, budget: createDownloadBudget(ctx) };
  const outcomes = await mapLimit(search.entries, ctx.concurrency || DOWNLOAD_CONCURRENCY, async (entry) => {
    try {
      return { skill: await downloadEntry(ctx, entry, destRoot) };
    } catch (e) {
      return {
        failure: {
          identifier: entry.identifier,
          plugin: entry.plugin.name,
          errorKind: e instanceof WorkIqError ? e.kind : "error",
          error: errorMessage(e),
          ...(e && e.requestId ? { requestId: e.requestId } : {}),
        },
      };
    }
  });
  const downloaded = outcomes.filter((o) => o.skill).map((o) => o.skill);
  downloaded.sort((a, b) => a.name.localeCompare(b.name, "en", { sensitivity: "base" }) || a.identifier.localeCompare(b.identifier));
  const skills = filterSkills(downloaded, query).map((s, i) => ({ n: i + 1, ...s }));
  return {
    available: search.available,
    truncated: search.truncated,
    downloaded: downloaded.length,
    count: skills.length,
    skills,
    failed: outcomes.filter((o) => o.failure).map((o) => o.failure),
    ...(search.requestId ? { requestId: search.requestId } : {}),
  };
}

// ---------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------

const { parseOptions, outputOptions, emitResult } = require("./helper-cli");
const VALUE_FLAGS = new Set(["query", "dest", "client-id", "tenant-id", "timeout", "limit", "offset", "output"]);
const BOOLEAN_FLAGS = new Set(["no-browser", "dry-run"]);

function parseArgs(argv) {
  try {
    return parseOptions(argv, { values: [...VALUE_FLAGS], booleans: [...BOOLEAN_FLAGS] });
  } catch (e) {
    throw inputError(e.message);
  }
}

function resolveOptions(args) {
  for (const key of Object.keys(args)) {
    if (key !== "_" && !VALUE_FLAGS.has(key) && !BOOLEAN_FLAGS.has(key)) throw inputError(`Unknown option --${key}.`);
  }
  const value = (flag) => {
    const v = args[flag];
    if (v === undefined) return undefined;
    if (typeof v !== "string") throw inputError(`--${flag} needs a value.`);
    return v.trim();
  };

  const clientId = value("client-id") || DEFAULT_CLIENT_ID;
  if (!GUID_RE.test(clientId)) throw inputError("--client-id must be an application (client) ID GUID.");
  const tenantId = value("tenant-id") || DEFAULT_TENANT;
  if (!TENANT_RE.test(tenantId)) throw inputError("--tenant-id must be a tenant GUID, a verified domain, or 'organizations'.");

  const query = value("query");
  if (query !== undefined && (!normalizeQuery(query) || query.length > 256 || CONTROL_RE.test(query))) {
    throw inputError("--query must be 1-256 characters with at least one letter or digit.");
  }

  const timeoutText = value("timeout");
  const timeoutSeconds = timeoutText === undefined ? DEFAULT_SIGN_IN_TIMEOUT_S : Number(timeoutText);
  if (!Number.isInteger(timeoutSeconds) || timeoutSeconds < 30 || timeoutSeconds > 900) {
    throw inputError("--timeout must be a whole number of seconds from 30 to 900.");
  }

  const dest = path.resolve(value("dest") || os.tmpdir());
  return {
    clientId,
    tenantId,
    query: query || null,
    timeoutSeconds,
    dest,
    noBrowser: Boolean(args["no-browser"]),
    dryRun: Boolean(args["dry-run"]),
  };
}

function emit(obj) {
  process.stdout.write(JSON.stringify(obj, null, 2) + "\n");
}

async function main(argv = process.argv.slice(2), deps = {}) {
  if (!argv.length || argv.includes("--help") || argv.includes("-h")) {
    process.stdout.write(`Usage: node workiq-catalog.bundle.js list [options]
  --query <text>          Filter acquired skills locally
  --dest <dir>            Private download parent (default: OS temporary directory)
  --client-id <guid>      OAuth public-client app ID
  --tenant-id <id>        Tenant GUID/domain or organizations
  --timeout <30-900>      Sign-in timeout in seconds (default: ${DEFAULT_SIGN_IN_TIMEOUT_S})
  --no-browser           Print the sign-in link without opening a browser
  --dry-run              Show the plan without authentication, downloads or writes
  --limit <1-100>         Results per stdout array page (default: 20)
  --offset <n>           Array page offset
  --output <new-file>     Complete JSON results; never overwrites
  --help                 Show this help without signing in
Listing authenticates and downloads up to the catalog acquisition limit.
Pagination bounds stdout, not downloads; use pages totals/full output for all results.
Exit codes: 0 success/help, 1 input/authentication/acquisition failure.
`);
    return;
  }
  const args = parseArgs(argv);
  if (args._[0] !== "list" || args._.length > 1) {
    throw inputError(`Unknown command: ${args._.join(" ") || "(none)"}. Use "list".`);
  }
  const opts = resolveOptions(args);
  let out;
  try { out = outputOptions(args); } catch (e) { throw inputError(e.message); }
  if (opts.dryRun && out.output) throw inputError("--dry-run cannot be combined with --output.");
  const searchUrl = new URL(SEARCH_PATH, BASE_URL);
  searchUrl.searchParams.set("type", SKILL_TYPE);

  if (opts.dryRun) {
    emit({
      ok: true, ...SOURCE, dryRun: true, command: "list",
      clientId: opts.clientId, tenantId: opts.tenantId, authority: authorityBase(opts.tenantId),
      scope: SCOPE, redirectUri: "http://localhost:<random port>", searchUrl: searchUrl.toString(),
      dest: opts.dest, timeoutSeconds: opts.timeoutSeconds, query: opts.query,
    });
    return;
  }

  const token = await signIn(
    { clientId: opts.clientId, tenantId: opts.tenantId, timeoutMs: opts.timeoutSeconds * 1000, noBrowser: opts.noBrowser },
    deps
  );
  log("Listing your WorkIQ skills...");
  const ctx = { baseUrl: BASE_URL, token, fetchImpl: deps.fetchImpl, sleep: deps.sleep };
  const destRoot = createRunDirectory(opts.dest);
  let result;
  try {
    result = await listSkills(ctx, { query: opts.query, destRoot });
  } catch (e) {
    fs.rmSync(destRoot, { recursive: true, force: true });
    throw e;
  }
  if (!result.downloaded) fs.rmSync(destRoot, { recursive: true, force: true });
  emitResult({ ok: true, ...SOURCE, query: opts.query, dest: destRoot, ...result }, out);
}

function failureOutput(e) {
  return {
    ok: false,
    ...SOURCE,
    errorKind: e instanceof WorkIqError ? e.kind : "error",
    ...(e && e.status !== undefined ? { status: e.status } : {}),
    error: errorMessage(e),
    ...(e && e.requestId ? { requestId: e.requestId } : {}),
    at: new Date().toISOString(),
  };
}

if (require.main === module) {
  main().catch((e) => {
    emit(failureOutput(e));
    process.exitCode = 1;
  });
}

module.exports = {
  BASE_URL,
  DEFAULT_CLIENT_ID,
  SCOPE,
  SKILL_TYPE,
  WorkIqError,
  assertWorkIqUrl,
  buildAuthorizeUrl,
  createPkce,
  createRunDirectory,
  downloadEntry,
  exchangeCode,
  failureOutput,
  filterSkills,
  httpError,
  listSkills,
  main,
  normalizeEntries,
  openBrowser,
  parseArgs,
  parseFrontmatter,
  request,
  resolveOptions,
  sanitizeFolderName,
  searchSkills,
  signIn,
  startLoopback,
};
