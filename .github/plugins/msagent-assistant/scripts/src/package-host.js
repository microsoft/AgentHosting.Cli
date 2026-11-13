const fs = require("node:fs");
const path = require("node:path");
const { createHash } = require("node:crypto");
const yaml = require("js-yaml");
const { parseOptions } = require("./helper-cli");

const ROOT = path.resolve(__dirname, "..", "..");
const CONTENT = [
  ".claude-plugin", "adapters", "agents", "commands", "evals", "hooks",
  "scripts", "skills", "README.md", "COMPATIBILITY.md", "LICENSE", "source.json",
];

function document(file) {
  const text = fs.readFileSync(file, "utf8");
  const match = text.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n([\s\S]*)$/);
  if (!match) throw new Error(`Missing frontmatter: ${file}`);
  return { metadata: yaml.load(match[1], { schema: yaml.JSON_SCHEMA }), body: match[2] };
}

function copy(source, destination, hash, relative = "") {
  const stat = fs.lstatSync(source);
  if (stat.isSymbolicLink()) throw new Error(`Distribution cannot contain a symlink: ${source}`);
  if (stat.isDirectory()) {
    fs.mkdirSync(destination);
    for (const name of fs.readdirSync(source).sort()) {
      if (["node_modules", "dist", ".git"].includes(name)) continue;
      copy(path.join(source, name), path.join(destination, name), hash, `${relative}/${name}`);
    }
  } else if (stat.isFile()) {
    const bytes = fs.readFileSync(source);
    hash.update(JSON.stringify(relative)).update("\0").update(bytes).update("\0");
    fs.writeFileSync(destination, bytes);
  } else {
    throw new Error(`Unsupported distribution entry: ${source}`);
  }
}

function writeJson(file, value) {
  fs.writeFileSync(file, JSON.stringify(value, null, 2) + "\n");
}

function packageHost({ target, output, root = ROOT }) {
  if (!["gemini", "agency"].includes(target)) {
    throw new Error("Choose --target gemini or agency. M365 is deferred: no independently usable skill subset.");
  }
  if (!output) throw new Error("--output must name a new package directory.");
  const destination = path.resolve(output);
  if (path.basename(destination) !== "msagent-assistant") {
    throw new Error("The generated package directory must be named msagent-assistant.");
  }
  const realRoot = fs.realpathSync(root);
  const parent = path.dirname(destination);
  const missing = [];
  let existing = destination;
  while (!fs.existsSync(existing)) {
    missing.unshift(path.basename(existing));
    const next = path.dirname(existing);
    if (next === existing) throw new Error(`No existing ancestor for ${destination}`);
    existing = next;
  }
  const realDestination = path.join(fs.realpathSync(existing), ...missing);
  const relative = path.relative(realRoot, realDestination);
  if (!relative || (!relative.startsWith(".." + path.sep) && !path.isAbsolute(relative) && relative !== "..")) {
    throw new Error("Output must be outside the canonical plugin directory.");
  }
  if (fs.existsSync(destination)) throw new Error(`Refusing to overwrite ${destination}`);
  fs.mkdirSync(parent, { recursive: true });
  const stage = fs.mkdtempSync(path.join(parent, ".msagent-assistant-"));
  try {
    const hash = createHash("sha256");
    for (const name of CONTENT) copy(path.join(root, name), path.join(stage, name), hash, name);
    const manifest = JSON.parse(fs.readFileSync(path.join(stage, ".claude-plugin", "plugin.json"), "utf8"));
    const provenance = JSON.parse(fs.readFileSync(path.join(stage, "source.json"), "utf8"));
    const adapter = fs.readFileSync(path.join(stage, "adapters", "README.md"), "utf8");
    if (target === "agency") {
      writeJson(path.join(stage, "plugin.json"), {
        $schema: "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json",
        ...manifest,
      });
      fs.rmSync(path.join(stage, ".claude-plugin"), { recursive: true });
      const copilot = path.join(stage, "com.github.copilot");
      fs.mkdirSync(copilot);
      for (const component of ["commands", "agents"]) {
        const directory = path.join(copilot, component);
        fs.renameSync(path.join(stage, component), directory);
        for (const name of fs.readdirSync(directory)) {
          if (!name.endsWith(".md")) continue;
          const file = path.join(directory, name);
          const text = fs.readFileSync(file, "utf8").replace(/\]\(\.\.\//g, "](../../");
          const destination = component === "agents" ? file.replace(/\.md$/, ".agent.md") : file;
          fs.writeFileSync(destination, text);
          if (destination !== file) fs.unlinkSync(file);
        }
      }
      const hookFile = path.join(stage, "hooks", "hooks.json");
      const startup = JSON.parse(fs.readFileSync(hookFile, "utf8")).hooks?.SessionStart?.[0]?.hooks?.[0];
      if (typeof startup?.bash !== "string" || typeof startup?.powershell !== "string") {
        throw new Error("Missing canonical Copilot startup launcher.");
      }
      fs.mkdirSync(path.join(copilot, "hooks"));
      writeJson(path.join(copilot, "hooks", "hooks.json"), {
        version: 1,
        hooks: {
          sessionStart: [{ type: "command", bash: startup.bash, powershell: startup.powershell }],
        },
      });
      fs.unlinkSync(hookFile);
      writeJson(path.join(stage, "agency.json"), {
        $schema: "https://raw.githubusercontent.com/agency-microsoft/.github-private/main/agency.schema.json",
        pluginId: provenance.agencyPluginId,
        engines: ["copilot"],
        category: "developer-tools",
        platforms: ["windows", "linux", "macos"],
      });
    } else {
      writeJson(path.join(stage, "gemini-extension.json"), {
        name: manifest.name,
        version: manifest.version,
        description: manifest.description,
        contextFileName: "GEMINI.md",
        settings: [{
          name: "Optional Graph public-client ID",
          description: "For the optional knowledge access check only. Leave empty to pass --client-id instead.",
          envVar: "MCS_KNOWLEDGE_CLIENT_ID",
          sensitive: false,
        }],
      });
      fs.writeFileSync(path.join(stage, "GEMINI.md"),
        "# Copilot Studio extension\n\n" +
        "Current host: Gemini CLI. Resolve plugin paths from this extension's installed directory, " +
        "not the project directory. Pass that exact root to delegated roles. " +
        "Commands and agents below are generated from the shared plugin source.\n\n" + adapter);
      for (const name of fs.readdirSync(path.join(stage, "commands"))) {
        if (!name.endsWith(".md")) continue;
        const file = path.join(stage, "commands", name);
        const { metadata, body } = document(file);
        const prompt = "Current host: Gemini CLI. Follow its mapping below.\n\n" + adapter +
          "\n\n" + body.replaceAll("$ARGUMENTS", "{{args}}");
        fs.writeFileSync(file.replace(/\.md$/, ".toml"),
          `description = ${JSON.stringify(metadata.description)}\nprompt = ${JSON.stringify(prompt)}\n`);
        fs.unlinkSync(file);
      }
      for (const name of fs.readdirSync(path.join(stage, "agents"))) {
        if (!name.endsWith(".md")) continue;
        const file = path.join(stage, "agents", name);
        const { metadata, body } = document(file);
        const fields = { name: name.slice(0, -3), description: metadata.description, kind: "local" };
        fs.writeFileSync(file, "---\n" + yaml.dump(fields, { lineWidth: -1 }) +
          "---\nCurrent host: Gemini CLI. Use its host mapping; do not delegate recursively.\n\n" +
          adapter + "\n\n" + body);
      }
      writeJson(path.join(stage, "hooks", "hooks.json"), {
        hooks: {
          SessionStart: [{
            hooks: [{
              name: "msagent-assistant-encrypted-cache",
              type: "command",
              command: "node -e \"const root=process.env.MCS_ASSISTANT_GEMINI_ROOT; if(!root) throw new Error('Missing Gemini extension root'); delete process.env.CLAUDE_ENV_FILE; require(require('node:path').join(root, 'hooks', 'set-env-vars.bundle.js'))\"",
              env: { MCS_ASSISTANT_GEMINI_ROOT: "${extensionPath}" },
            }],
          }],
        },
      });
    }
    writeJson(path.join(stage, "distribution.json"), {
      target,
      canonicalRepository: provenance.canonicalRepository,
      canonicalPath: provenance.canonicalPath,
      sourceHash: hash.digest("hex"),
      runtimeVerified: false,
    });
    fs.renameSync(stage, destination);
    return destination;
  } catch (error) {
    fs.rmSync(stage, { recursive: true, force: true });
    throw error;
  }
}

if (require.main === module) {
  try {
    const options = parseOptions(process.argv.slice(2), { values: ["target", "output"] });
    if (options._.length) throw new Error("Unexpected positional arguments.");
    console.log(packageHost({ target: options.target, output: options.output }));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}

module.exports = { packageHost };
