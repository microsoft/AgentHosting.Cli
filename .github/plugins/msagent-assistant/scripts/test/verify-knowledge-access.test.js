const assert = require("node:assert/strict");
const test = require("node:test");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  authorityHostForCloud,
  buildMsalConfig,
  classifyUrl,
  defaultGraphScopes,
  inferCloudFromUrl,
  normalizeCloud,
  resolveClientId,
  resolveSecureCachePlugin,
} = require("../src/verify-knowledge-access");
const { createSecureCachePlugin } = require("../src/secure-msal-cache");

test("knowledge registration is independently configured and explicit IDs take precedence", (t) => {
  const data = fs.mkdtempSync(path.join(os.tmpdir(), "knowledge-config-"));
  const names = [
    "MCS_ASSISTANT_PLUGIN_ROOT", "MCS_ASSISTANT_PLUGIN_DATA", "MCS_KNOWLEDGE_CLIENT_ID",
    "CLAUDE_PLUGIN_ROOT", "CLAUDE_PLUGIN_DATA", "COPILOT_PLUGIN_ROOT", "COPILOT_PLUGIN_DATA",
  ];
  const previous = Object.fromEntries(names.map((name) => [name, process.env[name]]));
  t.after(() => {
    for (const [name, value] of Object.entries(previous)) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
    fs.rmSync(data, { recursive: true, force: true });
  });
  for (const name of names) delete process.env[name];
  process.env.MCS_ASSISTANT_PLUGIN_ROOT = path.resolve(__dirname, "..", "..");
  process.env.MCS_ASSISTANT_PLUGIN_DATA = data;
  const registration = { agents: { selected: { appClientId: "knowledge-id" } } };
  fs.writeFileSync(path.join(data, "chat-config.json"), JSON.stringify(registration));
  assert.equal(resolveClientId({ agentId: "selected" }), null);
  fs.writeFileSync(path.join(data, "knowledge-config.json"), JSON.stringify(registration));
  assert.equal(resolveClientId({ agentId: "selected" }), "knowledge-id");
  process.env.MCS_KNOWLEDGE_CLIENT_ID = "environment-id";
  assert.equal(resolveClientId({ agentId: "selected" }), "environment-id");
  assert.equal(resolveClientId({ explicit: "explicit-id", agentId: "selected" }), "explicit-id");
});

test("maps sovereign clouds to their Microsoft Entra authority hosts", () => {
  assert.equal(authorityHostForCloud("Prod"), "login.microsoftonline.com");
  assert.equal(authorityHostForCloud("High"), "login.microsoftonline.us");
  assert.equal(authorityHostForCloud("DoD"), "login.microsoftonline.us");
  assert.equal(authorityHostForCloud("Mooncake"), "login.partner.microsoftonline.cn");
});

test("infers sovereign clouds from SharePoint hosts", () => {
  assert.equal(
    inferCloudFromUrl("https://contoso.sharepoint.us/sites/Operations"),
    "High"
  );
  assert.equal(
    inferCloudFromUrl("https://contoso.sharepoint-mil.us/sites/Operations"),
    "DoD"
  );
  assert.equal(
    inferCloudFromUrl("https://contoso.sharepoint.cn/sites/Operations"),
    "Mooncake"
  );
  assert.equal(inferCloudFromUrl("https://contoso.sharepoint.com/sites/Operations"), null);
});

test("does not silently map an unknown cloud to the global cloud", () => {
  assert.equal(normalizeCloud("not-a-cloud"), null);
});

test("uses the documented least-privileged delegated scope for Graph shares", () => {
  assert.deepEqual(defaultGraphScopes("graph.microsoft.com"), [
    "https://graph.microsoft.com/Files.ReadWrite",
  ]);
  assert.deepEqual(defaultGraphScopes("graph.microsoft.us"), [
    "https://graph.microsoft.us/Files.ReadWrite",
  ]);
});

test("rejects non-HTTPS SharePoint URLs", () => {
  assert.deepEqual(classifyUrl("http://contoso.sharepoint.com/sites/Operations"), {
    kind: "invalid",
    reason: "SharePoint and OneDrive URLs must use HTTPS.",
  });
});

test("rejects SharePoint URLs containing embedded credentials", () => {
  assert.deepEqual(
    classifyUrl("https://user:password@contoso.sharepoint.com/sites/Operations"),
    {
      kind: "invalid",
      reason: "SharePoint and OneDrive URLs must not contain embedded credentials.",
    }
  );
});

test("falls back to an in-memory cache when secure persistence is unavailable", async () => {
  const warnings = [];
  const cachePlugin = await resolveSecureCachePlugin(
    "chat-agent",
    (message) => warnings.push(message),
    async () => {
      throw new Error("secure storage unavailable");
    }
  );

  assert.equal(cachePlugin, null);
  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /in-memory token cache/i);
});

test("omits MSAL persistence when secure storage is unavailable", () => {
  assert.deepEqual(
    buildMsalConfig({
      clientId: "client-id",
      authority: "https://login.microsoftonline.us/tenant-id",
      cachePlugin: null,
    }),
    {
      auth: {
        clientId: "client-id",
        authority: "https://login.microsoftonline.us/tenant-id",
      },
    }
  );
});

test("disables plaintext Linux persistence in the secure cache", async () => {
  let persistenceOptions;
  class FakePersistenceCachePlugin {
    constructor(persistence) {
      this.persistence = persistence;
    }
  }

  const cachePlugin = await createSecureCachePlugin("chat-agent", () => ({
    PersistenceCreator: {
      createPersistence: async (options) => {
        persistenceOptions = options;
        return "encrypted-persistence";
      },
    },
    PersistenceCachePlugin: FakePersistenceCachePlugin,
    DataProtectionScope: { CurrentUser: "current-user" },
  }));

  assert.equal(persistenceOptions.usePlaintextFileOnLinux, false);
  assert.equal(cachePlugin.persistence, "encrypted-persistence");
});
