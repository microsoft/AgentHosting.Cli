// src/convert-actions-to-tools.js
var fs = require("node:fs");
var path = require("node:path");
var crypto = require("node:crypto");
var TYPE_MAP = {
  String: "string",
  Number: "number",
  Integer: "integer",
  Boolean: "boolean",
  Date: "string",
  DateTime: "string",
  Any: void 0
};
function usage() {
  console.error(
    [
      "Usage:",
      "  node convert-actions-to-tools.bundle.js <legacy-actions-folder> <new-tools-folder> [--all] [--include <action-file...>] [--exclude <action-file...>] [--report <path>] [--clean]",
      "  node convert-actions-to-tools.bundle.js <legacy-actions-folder> --list [--include <action-file...>] [--exclude <action-file...>] [--report <path>]",
      "",
      "Flow actions are resolved from the workflows folder next to the legacy actions folder.",
      "",
      "Examples:",
      '  node convert-actions-to-tools.bundle.js "C:\\Users\\gughini\\CopilotStudio\\SST\\actions" "C:\\Users\\gughini\\CopilotStudio\\dragent\\Giorgio2Clone\\capabilities\\tools" --clean',
      '  node convert-actions-to-tools.bundle.js "C:\\Users\\gughini\\CopilotStudio\\SST\\actions" "C:\\Users\\gughini\\CopilotStudio\\dragent\\Giorgio2Clone\\capabilities\\tools" --include "SQLServer-ExecuteaSQLqueryV2.mcs.yml" "Dataverse-Listrows.mcs.yml" --report tools-report.json',
      '  node convert-actions-to-tools.bundle.js "C:\\Users\\gughini\\CopilotStudio\\SST\\actions" --list --report action-inventory.json'
    ].join("\n")
  );
}
function countIndent(line) {
  const match = line.match(/^ */);
  return match ? match[0].length : 0;
}
function stripInlineComment(value) {
  let inSingle = false;
  let inDouble = false;
  for (let index = 0; index < value.length; index += 1) {
    const char = value[index];
    const previous = value[index - 1];
    if (char === "'" && !inDouble) inSingle = !inSingle;
    if (char === '"' && !inSingle && previous !== "\\") inDouble = !inDouble;
    if (char === "#" && !inSingle && !inDouble && /\s/.test(previous || "")) {
      return value.slice(0, index).trimEnd();
    }
  }
  return value;
}
function parseScalar(rawValue) {
  const value = stripInlineComment(rawValue.trim());
  if (value === "") return "";
  if (value === "null" || value === "~") return null;
  if (value === "true") return true;
  if (value === "false") return false;
  if (/^-?\d+(\.\d+)?$/.test(value)) return Number(value);
  if (value.startsWith('"') && value.endsWith('"')) {
    try {
      return JSON.parse(value);
    } catch {
      return value.slice(1, -1);
    }
  }
  if (value.startsWith("'") && value.endsWith("'")) {
    return value.slice(1, -1).replace(/''/g, "'");
  }
  return value;
}
function splitKeyValue(content) {
  let inSingle = false;
  let inDouble = false;
  for (let index = 0; index < content.length; index += 1) {
    const char = content[index];
    const previous = content[index - 1];
    if (char === "'" && !inDouble) inSingle = !inSingle;
    if (char === '"' && !inSingle && previous !== "\\") inDouble = !inDouble;
    if (char === ":" && !inSingle && !inDouble) {
      return [content.slice(0, index).trim(), content.slice(index + 1)];
    }
  }
  return [content.trim(), void 0];
}
function parseKey(rawKey) {
  return String(parseScalar(rawKey));
}
function prepareLines(text) {
  return text.replace(/^\uFEFF/, "").split(/\r?\n/).map((raw) => ({ raw, indent: countIndent(raw), text: raw.trim() })).filter((line) => line.text !== "" && !line.text.startsWith("#"));
}
function parseYaml(text) {
  const lines = prepareLines(text);
  const [value] = parseBlock(lines, 0, 0);
  return value || {};
}
function parseBlock(lines, index, indent) {
  const line = lines[index];
  if (!line || line.indent < indent) return [void 0, index];
  return line.text.startsWith("- ") && line.indent === indent ? parseArray(lines, index, indent) : parseMap(lines, index, indent);
}
function parseArray(lines, index, indent) {
  const result = [];
  let cursor = index;
  while (cursor < lines.length) {
    const line = lines[cursor];
    if (line.indent < indent) break;
    if (line.indent !== indent || !line.text.startsWith("- ")) break;
    const itemText = line.text.slice(2).trim();
    cursor += 1;
    if (itemText === "") {
      const [child, next] = parseBlock(lines, cursor, indent + 2);
      result.push(child);
      cursor = next;
      continue;
    }
    const [key, rawValue] = splitKeyValue(itemText);
    if (rawValue !== void 0) {
      const item = {};
      item[parseKey(key)] = rawValue.trim() === "" ? parseBlock(lines, cursor, indent + 2)[0] : parseScalar(rawValue);
      if (rawValue.trim() === "") {
        cursor = parseBlock(lines, cursor, indent + 2)[1];
      }
      const [extra, next] = parseMap(lines, cursor, indent + 2);
      Object.assign(item, extra || {});
      result.push(item);
      cursor = next;
    } else {
      result.push(parseScalar(itemText));
    }
  }
  return [result, cursor];
}
function parseMap(lines, index, indent) {
  const result = {};
  let cursor = index;
  while (cursor < lines.length) {
    const line = lines[cursor];
    if (line.indent < indent) break;
    if (line.indent !== indent || line.text.startsWith("- ")) break;
    const [rawKey, rawValue] = splitKeyValue(line.text);
    if (rawValue === void 0) break;
    cursor += 1;
    const key = parseKey(rawKey);
    if (rawValue.trim() === "") {
      const [child, next] = parseBlock(lines, cursor, indent + 2);
      result[key] = child;
      cursor = next;
    } else {
      result[key] = parseScalar(rawValue);
    }
  }
  return [result, cursor];
}
function quoteYaml(value) {
  if (typeof value !== "string") return String(value);
  if (/^[A-Za-z0-9 _./:@-]+$/.test(value) && !/^(true|false|null|~|-?\d+(\.\d+)?)$/.test(value)) {
    return value;
  }
  return JSON.stringify(value);
}
function schemaForLegacyType(type) {
  if (!type) return void 0;
  if (typeof type === "string") {
    const jsonType = TYPE_MAP[type] || type.toLowerCase();
    if (!jsonType) return void 0;
    if (type === "Date") return { type: jsonType, format: "date" };
    if (type === "DateTime") return { type: jsonType, format: "date-time" };
    return { type: jsonType };
  }
  if (type.kind === "Record") return { type: "object" };
  if (type.kind === "Table") return { type: "array" };
  return void 0;
}
function splitPropertyPath(propertyName) {
  const segments = [];
  let current = "";
  let inSingle = false;
  let inDouble = false;
  for (let index = 0; index < propertyName.length; index += 1) {
    const char = propertyName[index];
    const previous = propertyName[index - 1];
    if (char === "'" && !inDouble) {
      inSingle = !inSingle;
      continue;
    }
    if (char === '"' && !inSingle && previous !== "\\") {
      inDouble = !inDouble;
      continue;
    }
    if (char === "." && !inSingle && !inDouble) {
      segments.push(current);
      current = "";
      continue;
    }
    current += char;
  }
  if (current) segments.push(current);
  return segments;
}
function findInputSchema(root, propertyName) {
  if (!root || !root.properties || !propertyName) return void 0;
  let node = root;
  for (const segment of splitPropertyPath(propertyName)) {
    const properties = node.properties || node.type?.properties;
    if (!properties || !properties[segment]) return void 0;
    node = properties[segment];
  }
  return node;
}
function inferInputSchema(action, input) {
  const schemaNode = findInputSchema(action.dynamicInputSchema, input.propertyName);
  return schemaForLegacyType(schemaNode?.type);
}
function inferConnectorId(connectionReference) {
  const match = String(connectionReference || "").match(/(?:^|\.)(shared_[A-Za-z0-9_]+)(?:\.|$)/);
  return match ? `/providers/Microsoft.PowerApps/apis/${match[1]}` : void 0;
}
function stableSuffix(inputFileName) {
  return crypto.createHash("sha256").update(inputFileName).digest("hex").slice(0, 6);
}
function outputFileName(inputFileName) {
  const specialExtension = inputFileName.endsWith(".mcs.yml") ? ".mcs.yml" : inputFileName.endsWith(".mcs.yaml") ? ".mcs.yaml" : path.extname(inputFileName);
  const baseName = inputFileName.slice(0, inputFileName.length - specialExtension.length);
  return `${baseName}_${stableSuffix(inputFileName)}${specialExtension}`;
}
function baseMetadata(document, fallbackName) {
  const metadata = document["mcs.metadata"] || {};
  return {
    componentName: metadata.componentName || document.modelDisplayName || fallbackName,
    description: metadata.description || document.modelDescription
  };
}
function unsupportedWorkflow(reason) {
  return { skipped: reason, supportStatus: "unsupported" };
}
function resolveWorkflowPackage(sourceFolder2, flowId) {
  const workflowsFolder = path.join(path.dirname(sourceFolder2), "workflows");
  if (!fs.existsSync(workflowsFolder) || !fs.statSync(workflowsFolder).isDirectory()) {
    return unsupportedWorkflow(`Could not find sibling workflows folder for flow '${flowId}'`);
  }
  let workflowFolders;
  try {
    workflowFolders = fs.readdirSync(workflowsFolder, { withFileTypes: true }).filter((entry) => entry.isDirectory() && entry.name.toLowerCase().endsWith(`-${flowId.toLowerCase()}`));
  } catch (error) {
    return unsupportedWorkflow(`Could not inspect workflows folder for flow '${flowId}': ${error.message}`);
  }
  if (workflowFolders.length === 0) {
    return unsupportedWorkflow(`Could not match flow '${flowId}' to a workflow folder by ID suffix`);
  }
  if (workflowFolders.length > 1) {
    return unsupportedWorkflow(`Flow '${flowId}' matched multiple workflow folders by ID suffix`);
  }
  const workflowFolder = path.join(workflowsFolder, workflowFolders[0].name);
  const workflowJsonPath = path.join(workflowFolder, "workflow.json");
  if (!fs.existsSync(workflowJsonPath) || !fs.statSync(workflowJsonPath).isFile()) {
    return unsupportedWorkflow(`Matched workflow folder for flow '${flowId}' has no workflow.json`);
  }
  let workflow;
  try {
    const workflowText = fs.readFileSync(workflowJsonPath, "utf8").replace(/^\uFEFF/, "");
    workflow = JSON.parse(workflowText);
  } catch (error) {
    return unsupportedWorkflow(`Could not parse workflow.json for flow '${flowId}': ${error.message}`);
  }
  const metadataPath = ["metadata.yml", "metadata.yaml"].map((fileName) => path.join(workflowFolder, fileName)).find((candidate) => fs.existsSync(candidate) && fs.statSync(candidate).isFile());
  let metadata = {};
  if (metadataPath) {
    try {
      metadata = parseYaml(fs.readFileSync(metadataPath, "utf8"));
    } catch (error) {
      return unsupportedWorkflow(`Could not parse workflow metadata for flow '${flowId}': ${error.message}`);
    }
  }
  return {
    workflow,
    metadata,
    workflowFolder,
    workflowJsonPath
  };
}
function workflowInputDefinitions(workflow) {
  const schema = workflow?.properties?.definition?.triggers?.manual?.inputs?.schema;
  const properties = schema?.properties;
  if (!properties || typeof properties !== "object" || Array.isArray(properties)) {
    return void 0;
  }
  return Object.entries(properties).map(([propertyName, property]) => {
    const displayName = property?.title || propertyName;
    return {
      name: displayName,
      displayName,
      description: property?.description
    };
  });
}
function workflowOutputDefinitions(workflow) {
  const outputs = /* @__PURE__ */ new Map();
  function visit(node) {
    if (!node || typeof node !== "object") return;
    if (node.type === "Response" && node.kind === "Skills") {
      const properties = node.inputs?.schema?.properties;
      if (properties && typeof properties === "object" && !Array.isArray(properties)) {
        for (const propertyName of Object.keys(properties)) {
          outputs.set(propertyName, { name: propertyName });
        }
      }
    }
    for (const value of Object.values(node)) {
      visit(value);
    }
  }
  visit(workflow?.properties?.definition?.actions);
  return [...outputs.values()];
}
function convertFlowAction(document, action, sourceFolder2) {
  if (!action.flowId) {
    return { skipped: "Flow action is missing flowId" };
  }
  const workflowPackage = resolveWorkflowPackage(sourceFolder2, String(action.flowId));
  if (workflowPackage.skipped) {
    return workflowPackage;
  }
  const inputs = workflowInputDefinitions(workflowPackage.workflow);
  if (!inputs) {
    return unsupportedWorkflow(`Workflow '${action.flowId}' has no manual trigger input schema`);
  }
  const outputs = workflowOutputDefinitions(workflowPackage.workflow);
  const metadata = baseMetadata(document, "Converted workflow");
  const description = workflowPackage.metadata.description || metadata.description;
  const lines = [];
  lines.push("mcs.metadata:");
  lines.push(`  componentName: ${quoteYaml(metadata.componentName)}`);
  if (description) lines.push(`  description: ${quoteYaml(description)}`);
  lines.push("kind: WorkflowTool");
  lines.push(`workflowId: ${quoteYaml(String(action.flowId))}`);
  if (outputs.length > 0) {
    lines.push("toolOutputs:");
    for (const output of outputs) {
      lines.push(`  - name: ${quoteYaml(output.name)}`);
    }
  }
  if (inputs.length > 0) {
    lines.push("toolInputs:");
    for (const input of inputs) {
      lines.push(`  - name: ${quoteYaml(input.name)}`);
      lines.push(`    displayName: ${quoteYaml(input.displayName)}`);
      if (input.description) lines.push(`    description: ${quoteYaml(input.description)}`);
    }
  }
  return {
    yaml: `${lines.join("\n")}
`,
    workflow: {
      workflowId: String(action.flowId),
      workflowFolder: workflowPackage.workflowFolder,
      workflowJsonPath: workflowPackage.workflowJsonPath,
      inputs,
      outputs
    }
  };
}
function convertConnectorAction(document, action) {
  const connectionReference = action.connectionReference;
  const connectorId = inferConnectorId(connectionReference);
  if (!connectionReference || !connectorId || !action.operationId) {
    return { skipped: "Connector action is missing connectionReference, connectorId inference, or operationId" };
  }
  const metadata = baseMetadata(document, "Converted connector action");
  const authMode = action.connectionProperties?.mode || "Invoker";
  const inputs = Array.isArray(document.inputs) ? document.inputs : [];
  const lines = [];
  lines.push("mcs.metadata:");
  lines.push(`  componentName: ${quoteYaml(metadata.componentName)}`);
  if (metadata.description) lines.push(`  description: ${quoteYaml(metadata.description)}`);
  lines.push("kind: ConnectorTool");
  lines.push(`authMode: ${quoteYaml(authMode)}`);
  lines.push(`connectionReference: ${quoteYaml(connectionReference)}`);
  lines.push(`connectorId: ${quoteYaml(connectorId)}`);
  lines.push(`operationId: ${quoteYaml(action.operationId)}`);
  if (inputs.length > 0) {
    lines.push("toolInputs:");
    for (const input of inputs) {
      if (!input.propertyName) continue;
      lines.push(`  - name: ${quoteYaml(input.propertyName)}`);
      lines.push("    value:");
      lines.push("      kind: ValueReference");
      const schema = inferInputSchema(action, input);
      if (schema) lines.push(`      type: ${quoteYaml(JSON.stringify(schema))}`);
      if (Object.prototype.hasOwnProperty.call(input, "value")) {
        lines.push(`      defaultValue: ${quoteYaml(JSON.stringify(input.value))}`);
      }
      lines.push("");
    }
    if (lines[lines.length - 1] === "") lines.pop();
  }
  return { yaml: `${lines.join("\n")}
` };
}
function convertMcpAction(document, action) {
  const connectionReference = action.connectionReference;
  const connectorId = inferConnectorId(connectionReference);
  const operationId = action.operationDetails?.operationId;
  if (!connectionReference || !connectorId || !operationId) {
    return { skipped: "MCP action is missing connectionReference, connectorId inference, or operationDetails.operationId" };
  }
  const metadata = baseMetadata(document, "Converted MCP server");
  const displayName = document.modelDisplayName || metadata.componentName;
  const componentName = `${displayName} \u2014 ${displayName}`;
  const authMode = action.connectionProperties?.mode || "Invoker";
  const lines = [];
  lines.push("mcs.metadata:");
  lines.push(`  componentName: ${quoteYaml(componentName)}`);
  if (metadata.description) lines.push(`  description: ${quoteYaml(metadata.description)}`);
  lines.push("kind: McpTool");
  lines.push(`authMode: ${quoteYaml(authMode)}`);
  lines.push(`connectionReference: ${quoteYaml(connectionReference)}`);
  lines.push(`connectorId: ${quoteYaml(connectorId)}`);
  lines.push(`operationId: ${quoteYaml(operationId)}`);
  return { yaml: `${lines.join("\n")}
` };
}
function convertTaskDialog(document, sourceFolder2) {
  if (document.kind !== "TaskDialog") {
    return { skipped: `Unsupported top-level kind '${document.kind || "unknown"}'` };
  }
  const action = document.action || {};
  if (action.kind === "InvokeConnectorTaskAction") {
    return convertConnectorAction(document, action);
  }
  if (action.kind === "InvokeExternalAgentTaskAction") {
    return convertMcpAction(document, action);
  }
  if (action.kind === "InvokeFlowTaskAction") {
    return convertFlowAction(document, action, sourceFolder2);
  }
  if (action.kind === "InvokeAIBuilderModelTaskAction") {
    return { skipped: "AI prompt actions are not supported" };
  }
  return { skipped: `Unsupported action kind '${action.kind || "unknown"}'` };
}
function actionOperationId(action) {
  return action.operationId || action.operationDetails?.operationId;
}
function supportStatus(result) {
  if (!result.skipped) return "convertible";
  if (result.supportStatus) return result.supportStatus;
  if (result.skipped.includes("missing")) return "invalid";
  return "unsupported";
}
function inventoryInput(input) {
  const result = {
    kind: input.kind,
    propertyName: input.propertyName
  };
  if (Object.prototype.hasOwnProperty.call(input, "value")) {
    result.defaultValue = input.value;
  }
  return result;
}
function inventoryOutput(output) {
  return {
    propertyName: output.propertyName,
    name: output.name
  };
}
function inventoryEntry(fileName, document, result) {
  const mcsMetadata = document["mcs.metadata"] || {};
  const metadata = baseMetadata(document, "Unknown action");
  const action = document.action || {};
  const entry = {
    fileName,
    "mcs.metadata": mcsMetadata,
    componentName: metadata.componentName,
    modelDisplayName: document.modelDisplayName,
    modelDescription: document.modelDescription,
    kind: document.kind,
    actionKind: action.kind,
    flowId: action.flowId,
    connectionReference: action.connectionReference,
    connectorId: inferConnectorId(action.connectionReference),
    operationId: actionOperationId(action),
    inputs: Array.isArray(document.inputs) ? document.inputs.map(inventoryInput) : [],
    outputs: Array.isArray(document.outputs) ? document.outputs.map(inventoryOutput) : [],
    workflowInputs: result.workflow?.inputs,
    workflowOutputs: result.workflow?.outputs,
    workflowFolder: result.workflow?.workflowFolder,
    supportStatus: supportStatus(result)
  };
  if (result.skipped) {
    entry.reason = result.skipped;
  }
  return entry;
}
function readAction(sourceFolder2, fileName) {
  const sourcePath = path.join(sourceFolder2, fileName);
  const text = fs.readFileSync(sourcePath, "utf8");
  const document = parseYaml(text);
  const result = convertTaskDialog(document, sourceFolder2);
  return {
    document,
    result,
    inventory: inventoryEntry(fileName, document, result)
  };
}
function cleanOutputFolder(outputFolder2) {
  if (!fs.existsSync(outputFolder2)) return 0;
  let deleted2 = 0;
  for (const fileName of fs.readdirSync(outputFolder2)) {
    if (!/\.ya?ml$/i.test(fileName)) continue;
    fs.rmSync(path.join(outputFolder2, fileName), { force: true });
    deleted2 += 1;
  }
  return deleted2;
}
function optionValues(args, index, optionName) {
  const values = [];
  let cursor = index + 1;
  while (cursor < args.length && !args[cursor].startsWith("--")) {
    values.push(args[cursor]);
    cursor += 1;
  }
  if (values.length === 0) {
    throw new Error(`${optionName} requires at least one action file name.`);
  }
  return { values, nextIndex: cursor };
}
function parseArgs(args) {
  const options2 = {
    all: false,
    clean: false,
    exclude: [],
    folders: [],
    include: [],
    list: false,
    reportPath: void 0
  };
  for (let index = 0; index < args.length; ) {
    const arg = args[index];
    if (arg === "--all") {
      options2.all = true;
      index += 1;
      continue;
    }
    if (arg === "--clean") {
      options2.clean = true;
      index += 1;
      continue;
    }
    if (arg === "--list") {
      options2.list = true;
      index += 1;
      continue;
    }
    if (arg === "--report") {
      const reportPath = args[index + 1];
      if (!reportPath || reportPath.startsWith("--")) {
        throw new Error("--report requires a path.");
      }
      options2.reportPath = reportPath;
      index += 2;
      continue;
    }
    if (arg === "--include") {
      const { values, nextIndex } = optionValues(args, index, "--include");
      options2.include.push(...values);
      index = nextIndex;
      continue;
    }
    if (arg === "--exclude") {
      const { values, nextIndex } = optionValues(args, index, "--exclude");
      options2.exclude.push(...values);
      index = nextIndex;
      continue;
    }
    if (arg.startsWith("--")) {
      throw new Error(`Unknown option: ${arg}`);
    }
    options2.folders.push(arg);
    index += 1;
  }
  if (options2.all && options2.include.length > 0) {
    throw new Error("--all cannot be combined with --include.");
  }
  return options2;
}
function selectorFileName(selector) {
  return path.basename(selector);
}
function selectorSet(selectors, sourceFiles2, optionName) {
  const available = new Set(sourceFiles2);
  const normalized = selectors.map(selectorFileName);
  const unknown = [...new Set(normalized.filter((fileName) => !available.has(fileName)))];
  if (unknown.length > 0) {
    throw new Error(`${optionName} action file(s) not found: ${unknown.join(", ")}`);
  }
  return new Set(normalized);
}
function selectedSourceFiles(sourceFiles2, includeSet2, excludeSet2) {
  const selected2 = [];
  const excluded2 = [];
  for (const fileName of sourceFiles2) {
    if (includeSet2.size > 0 && !includeSet2.has(fileName)) {
      excluded2.push({ fileName, reason: "Not selected by --include" });
      continue;
    }
    if (excludeSet2.has(fileName)) {
      excluded2.push({ fileName, reason: "Excluded by --exclude" });
      continue;
    }
    selected2.push(fileName);
  }
  return { selected: selected2, excluded: excluded2 };
}
function formatInventoryValue(value) {
  if (value === void 0) return "(not set)";
  if (value === null) return "null";
  if (typeof value === "object") return JSON.stringify(value, null, 2);
  return String(value);
}
function indentInventoryValue(value, spaces) {
  const indentation = " ".repeat(spaces);
  return formatInventoryValue(value).replace(/\n/g, `
${indentation}`);
}
function printInventory(inventory2) {
  if (inventory2.length === 0) {
    console.log("No legacy action YAML files found.");
    return;
  }
  console.log("Legacy action inventory:");
  for (const entry of inventory2) {
    const actionKind = entry.actionKind || entry.kind || "unknown";
    const status = entry.supportStatus === "convertible" ? "convertible" : `${entry.supportStatus}: ${entry.reason}`;
    console.log(`- fileName: ${entry.fileName}`);
    console.log(`  supportStatus: ${status}`);
    console.log(`  action.kind: ${actionKind}`);
    console.log("  mcs.metadata:");
    console.log(`    ${indentInventoryValue(entry["mcs.metadata"], 4)}`);
    console.log(`  modelDisplayName: ${indentInventoryValue(entry.modelDisplayName, 22)}`);
    console.log(`  modelDescription: ${indentInventoryValue(entry.modelDescription, 22)}`);
    console.log(`  action.operationId: ${indentInventoryValue(entry.operationId, 22)}`);
    console.log(`  action.flowId: ${indentInventoryValue(entry.flowId, 17)}`);
  }
}
function writeReport(reportPath, report2) {
  const resolvedReportPath = path.resolve(reportPath);
  fs.mkdirSync(path.dirname(resolvedReportPath), { recursive: true });
  fs.writeFileSync(resolvedReportPath, `${JSON.stringify(report2, null, 2)}
`, "utf8");
  console.log(`Wrote report ${resolvedReportPath}`);
}
function reportFilters(options2, includeSet2, excludeSet2) {
  return {
    all: options2.all || includeSet2.size === 0,
    include: [...includeSet2],
    exclude: [...excludeSet2]
  };
}
var options;
try {
  options = parseArgs(process.argv.slice(2));
} catch (error) {
  console.error(error.message);
  usage();
  process.exit(1);
}
if (options.list) {
  if (options.folders.length < 1 || options.folders.length > 2 || options.clean) {
    usage();
    process.exit(1);
  }
} else if (options.folders.length !== 2) {
  usage();
  process.exit(1);
}
var sourceFolder = path.resolve(options.folders[0]);
var outputFolder = options.folders[1] ? path.resolve(options.folders[1]) : void 0;
if (!fs.existsSync(sourceFolder) || !fs.statSync(sourceFolder).isDirectory()) {
  console.error(`Source folder does not exist or is not a directory: ${sourceFolder}`);
  process.exit(1);
}
var sourceFiles = fs.readdirSync(sourceFolder).filter((fileName) => /\.ya?ml$/i.test(fileName)).sort((left, right) => left.localeCompare(right));
var includeSet;
var excludeSet;
try {
  includeSet = selectorSet(options.include, sourceFiles, "--include");
  excludeSet = selectorSet(options.exclude, sourceFiles, "--exclude");
} catch (error) {
  console.error(error.message);
  process.exit(1);
}
if (options.clean && (includeSet.size > 0 || excludeSet.size > 0)) {
  console.error("--clean cannot be used with --include or --exclude because it would delete tools outside the selected subset.");
  process.exit(1);
}
var { selected, excluded } = selectedSourceFiles(sourceFiles, includeSet, excludeSet);
var actionData = new Map(sourceFiles.map((fileName) => [fileName, readAction(sourceFolder, fileName)]));
var inventory = sourceFiles.map((fileName) => actionData.get(fileName).inventory);
var inventoryByFileName = new Map(inventory.map((entry) => [entry.fileName, entry]));
if (options.list) {
  const report2 = {
    mode: "list",
    sourceFolder,
    filters: reportFilters(options, includeSet, excludeSet),
    inventory,
    selectedFiles: selected,
    excluded: excluded.map(({ fileName, reason }) => ({
      ...inventoryByFileName.get(fileName),
      reason
    })),
    totals: {
      source: sourceFiles.length,
      selected: selected.length,
      excluded: excluded.length
    }
  };
  printInventory(inventory);
  if (options.reportPath) writeReport(options.reportPath, report2);
  process.exit(0);
}
fs.mkdirSync(outputFolder, { recursive: true });
var deleted = options.clean ? cleanOutputFolder(outputFolder) : 0;
var report = {
  mode: "migrate",
  sourceFolder,
  outputFolder,
  filters: reportFilters(options, includeSet, excludeSet),
  converted: [],
  skippedUnsupported: [],
  invalid: [],
  excluded: excluded.map(({ fileName, reason }) => ({
    ...inventoryByFileName.get(fileName),
    reason
  }))
};
for (const fileName of selected) {
  const { result, inventory: entry } = actionData.get(fileName);
  if (result.skipped) {
    const skippedEntry = { ...entry, reason: result.skipped };
    if (entry.supportStatus === "invalid") {
      report.invalid.push(skippedEntry);
    } else {
      report.skippedUnsupported.push(skippedEntry);
    }
    console.warn(`Skipped ${fileName}: ${result.skipped}`);
    continue;
  }
  const targetFileName = outputFileName(fileName);
  const targetPath = path.join(outputFolder, targetFileName);
  fs.writeFileSync(targetPath, result.yaml, "utf8");
  report.converted.push({
    ...entry,
    targetFileName,
    targetPath
  });
  console.log(`Converted ${fileName} -> ${targetFileName}`);
}
report.totals = {
  source: sourceFiles.length,
  selected: selected.length,
  converted: report.converted.length,
  skippedUnsupported: report.skippedUnsupported.length,
  invalid: report.invalid.length,
  excluded: report.excluded.length,
  deleted
};
console.log(
  `Done. Converted ${report.totals.converted}, skipped ${report.totals.skippedUnsupported + report.totals.invalid}, excluded ${report.totals.excluded}, deleted ${deleted}.`
);
if (options.reportPath) writeReport(options.reportPath, report);
