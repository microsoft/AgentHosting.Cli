'use strict';

const fs = require('fs');
const path = require('path');

function parseOptions(argv, { values = [], booleans = [], allowPositionalsAfterSwitches = false } = {}) {
  const args = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const token = argv[i];
    if (!token.startsWith('--')) {
      args._.push(token);
      continue;
    }
    const key = token.slice(2);
    if (Object.hasOwn(args, key)) throw new Error(`Duplicate option --${key}.`);
    if (booleans.includes(key)) {
      args[key] = true;
      const next = argv[i + 1];
      if (next !== undefined && !next.startsWith('--')
          && (!allowPositionalsAfterSwitches || /^(true|false)$/i.test(next))) {
        throw new Error(`--${key} is a boolean switch; do not supply a value.`);
      }
    } else if (values.includes(key)) {
      const value = argv[++i];
      if (!value || value.startsWith('--')) throw new Error(`--${key} needs a value.`);
      args[key] = value;
    } else {
      throw new Error(`Unknown option --${key}.`);
    }
  }
  return args;
}

function outputOptions(args) {
  const integer = (key, fallback, min, max) => {
    const value = args[key] === undefined ? fallback : Number(args[key]);
    if (!Number.isInteger(value) || value < min || value > max) throw new Error(`--${key} must be an integer from ${min} to ${max}.`);
    return value;
  };
  const output = args.output && path.resolve(args.output);
  if (output) {
    if (fs.existsSync(output)) throw new Error('--output must be a new file.');
    fs.accessSync(path.dirname(output), fs.constants.W_OK);
  }
  return {
    limit: integer('limit', 20, 1, 100),
    offset: integer('offset', 0, 0, Number.MAX_SAFE_INTEGER),
    output,
  };
}

function emitResult(result, opts = {}) {
  // Full machine-readable results stay available without flooding agent context.
  if (opts.output) fs.writeFileSync(opts.output, JSON.stringify(result, null, 2) + '\n', { flag: 'wx' });
  const summary = { ...result };
  const pages = {};
  for (const [key, value] of Object.entries(result)) {
    if (!Array.isArray(value)) continue;
    const offset = opts.offset || 0;
    const limit = opts.limit || 20;
    summary[key] = value.slice(offset, offset + limit);
    pages[key] = { total: value.length, offset, returned: summary[key].length, hasMore: offset + limit < value.length };
  }
  if (Object.keys(pages).length) summary.pages = pages;
  if (opts.output) summary.output = opts.output;
  while (Buffer.byteLength(JSON.stringify(summary, null, 2)) + 1 > 12000) {
    const largest = Object.keys(pages).filter((key) => summary[key].length)
      .sort((a, b) => JSON.stringify(summary[b]).length - JSON.stringify(summary[a]).length)[0];
    if (!largest) {
      process.stdout.write(JSON.stringify({
        ok: result.ok,
        status: result.status,
        output: opts.output,
        summaryOmitted: true,
        message: 'Result metadata exceeds the stdout budget. Use --output <new-file> for complete results.',
      }) + '\n');
      return;
    }
    summary[largest].pop();
    pages[largest].returned = summary[largest].length;
    pages[largest].hasMore = pages[largest].offset + pages[largest].returned < pages[largest].total;
    summary.truncatedForSize = true;
  }
  process.stdout.write(JSON.stringify(summary, null, 2) + '\n');
}

module.exports = { parseOptions, outputOptions, emitResult };
