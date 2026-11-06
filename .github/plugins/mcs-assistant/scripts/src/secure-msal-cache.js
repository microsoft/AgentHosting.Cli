/**
 * MSAL persistence that never permits a plaintext fallback.
 */

const path = require("path");
const { createHash } = require("crypto");
const { createRequire } = require("module");
const { resolvePluginPaths } = require("./plugin-paths");

async function createSecureCachePlugin(
  accountName,
  loadDependencies = () => createRequire(
    path.join(resolvePluginPaths().pluginData, "package.json")
  )("@azure/msal-node-extensions")
) {
  const {
    PersistenceCreator,
    PersistenceCachePlugin,
    DataProtectionScope,
  } = loadDependencies();

  const { pluginData } = resolvePluginPaths();
  const identity = createHash("sha256").update(pluginData).digest("hex").slice(0, 20);
  const persistence = await PersistenceCreator.createPersistence({
    cachePath: path.join(pluginData, `${accountName}.cache.json`),
    dataProtectionScope: DataProtectionScope.CurrentUser,
    serviceName: `copilot-studio-knowledge-${identity}`,
    accountName,
    usePlaintextFileOnLinux: false,
  });
  return new PersistenceCachePlugin(persistence);
}

module.exports = { createSecureCachePlugin };
