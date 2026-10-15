/*!
 * Copyright (C) Microsoft Corporation. All rights reserved.
 */

/**
 * AgentHost CLI — provisional TypeScript ABS conformance oracle runner (plan node cN1).
 *
 * WHAT THIS IS
 * ------------
 * A language-neutral, offline oracle that freezes the *scoped* shipped TypeScript Azure Bot
 * Service (ABS) behavior as a provisional baseline, BEFORE any C# ABS behavior exists. It drives
 * the REAL pinned production command core (`absPreviewAsync` / `absApplyAsync`) with injected fake
 * Azure dependencies and compares the observable result (exit code, JSON envelope, normalized
 * human output, and which mutation ports were invoked) against the fixtures in `../fixtures`.
 *
 * FAITHFULNESS / NON-INTRUSION
 * ----------------------------
 *   - The pinned TypeScript worktree is imported READ-ONLY and never modified. The oracle injects a
 *     fake `ProvisionCommandContext` (the same seam the pinned unit tests use) instead of the real
 *     `buildProvisionCommandContext`, so no MSAL provider is constructed, no browser/WAM prompt can
 *     open, and no ARM/Graph/Azure/identity network call is ever made.
 *   - The only production behavior reproduced here (rather than imported) is the thin, un-injectable
 *     shell mapping from a thrown error to a process exit code + JSON/stderr envelope (the shared R7
 *     failure envelope). That mapping mirrors `boot()` in the pinned `src/Cli.ts` on the `ah-start`
 *     branch (originally pinned at 5624d54, since advanced for the evergreen failure contract) and
 *     MUST be kept in sync with it. Everything else — argument validation, desired-state derivation,
 *     the review planner, the output contract, the redaction guard, and the human/JSON renderers —
 *     is the genuine pinned production code.
 *
 * SCOPE
 * -----
 * Every ABS fixture supplies `--msa-app-id`, so the pinned core takes the zero-Graph path (ARM
 * only). The fake Graph reads THROW, so if the production code ever regressed into issuing a Graph
 * read on this path the oracle would fail loudly.
 *
 * MODES
 * -----
 *   bun runOracle.ts                 verify: run every fixture and compare against its stored golden
 *   bun runOracle.ts --update        capture: (re)write each fixture's `golden` from the live run
 *   bun runOracle.ts --pinned <dir>  point at a specific read-only worktree (the AgentHostCli pkg
 *                                     dir that contains `src/` and `node_modules/`)
 *
 * The pinned worktree may also be selected with the AH_PINNED_TS_DIR environment variable.
 * `--pinned` overrides the env var, which overrides the built-in default.
 *
 * DETERMINISM
 * -----------
 * The runner fixes the clock (per fixture), forces non-TTY / NO_COLOR output, and uses fixed fake
 * ids, so repeated runs produce byte-identical goldens. Any nondeterminism is a failure.
 */

import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// ── Config ───────────────────────────────────────────────────────────────────────

const DEFAULT_PINNED_DIR = 'C:/CliAffordances/amh/src/AgentHostCli';

const THIS_DIR = dirname(fileURLToPath(import.meta.url));
const FIXTURES_DIR = resolve(THIS_DIR, '..', 'fixtures');

interface CliArgs {
  update: boolean;
  pinnedDir: string;
}

function parseCliArgs(argv: string[]): CliArgs {
  let update = false;
  let pinnedDir = process.env.AH_PINNED_TS_DIR?.trim() || DEFAULT_PINNED_DIR;
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--update') update = true;
    else if (a === '--pinned') pinnedDir = argv[++i] ?? pinnedDir;
    else if (a.startsWith('--pinned=')) pinnedDir = a.slice('--pinned='.length);
    else throw new Error(`Unknown runner argument: ${a}`);
  }
  return { update, pinnedDir };
}

// ── Language-neutral fixture + Azure-state shapes ──────────────────────────────────
//
// These mirror the pinned production types (src/Types/Provisioning.types.ts). They are declared
// here (not imported) because they are the neutral contract both the TypeScript oracle and the
// future C# contract runner encode independently.

type ProviderRegistrationState = 'Registered' | 'NotRegistered' | 'Registering' | 'Unknown';

interface ArmScopeAccess {
  exists: boolean;
  hasReadAccess: boolean;
}

interface ExistingBotService {
  botResourceId: string;
  name: string;
  endpoint?: string;
  msaAppId?: string;
  msaAppType?: string;
  sku?: string;
}

interface FixtureAzureState {
  providerRegistration: ProviderRegistrationState;
  subscription: ArmScopeAccess;
  resourceGroup: ArmScopeAccess;
  existingBot: ExistingBotService | null;
  /**
   * When true, every ARM read fails as interaction-required (a cached session that MSAL can no
   * longer refresh silently for the requested `--tenant`). The pinned ABS adapter reframes this as
   * the shared tenant-scoped exit-3 error; this fake reproduces it at the read seam.
   */
  targetTenantAuthFailure?: boolean;
  graphReadsMustNotHappen: boolean;
}

interface FixtureAssert {
  outcome: 'success' | 'error';
  exitCode: number;
  errorKind?: string | null;
  mutationCalls: string[];
}

interface Golden {
  json: unknown;
  human: { stdout: string[]; stderr: string[] };
}

interface Fixture {
  fixtureSchemaVersion: number;
  id: string;
  title: string;
  description?: string;
  note?: string;
  source?: unknown;
  argv: string[];
  azureState: FixtureAzureState;
  clock: string;
  assert: FixtureAssert;
  volatileFields?: string[];
  golden: Golden | null;
}

// ── Pinned production surface (dynamically imported, read-only) ────────────────────

interface PinnedSurface {
  absPreviewAsync: (ctx: unknown, options: unknown) => Promise<void>;
  absApplyAsync: (ctx: unknown, options: unknown) => Promise<void>;
  printJsonError: (message: string, exitCode?: number, details?: Record<string, unknown>) => void;
  printError: (message: string) => void;
  printHint: (message: string) => void;
  AuthError: new (...args: never[]) => Error & { exitCode: number; remediation?: string };
  ProvisioningError: new (...args: never[]) => Error & { kind: string; remediation: string };
  /**
   * The shared factory the pinned ABS adapter uses to surface a target-tenant silent-auth failure.
   * Re-used here so the oracle injects the byte-identical exit-3 envelope (tenant-scoped message +
   * remediation, no `errorKind`) rather than hand-rolling the strings — keeping the R7 failure
   * contract in one place across both the CLI and this oracle.
   */
  armTenantInteractionRequiredError: (
    tenantId: string
  ) => Error & { exitCode: number; remediation?: string };
  ConnectionMetadataStore: new (fs: unknown) => unknown;
}

async function loadPinnedSurface(pinnedDir: string): Promise<PinnedSurface> {
  const srcDir = join(pinnedDir, 'src');
  if (!existsSync(join(srcDir, 'Cli.ts'))) {
    throw new Error(
      `Pinned TypeScript source not found under "${srcDir}". Point the runner at the AgentHostCli ` +
        `package directory of the pinned read-only worktree with --pinned <dir> or AH_PINNED_TS_DIR ` +
        `(default: ${DEFAULT_PINNED_DIR}). The directory must contain src/ and node_modules/.`
    );
  }
  const imp = (rel: string): Promise<Record<string, unknown>> =>
    import(pathToFileURL(join(srcDir, ...rel.split('/'))).href) as Promise<
      Record<string, unknown>
    >;

  const [preview, apply, output, authErr, provErr, absAdapter, metaStore] = await Promise.all([
    imp('Verbs/AbsPreview.ts'),
    imp('Verbs/AbsApply.ts'),
    imp('Utils/ConsoleOutput.ts'),
    imp('Errors/AuthError.ts'),
    imp('Errors/ProvisioningError.ts'),
    imp('Targets/AbsProvisioningAdapter.ts'),
    imp('Provisioning/ConnectionMetadataStore.ts'),
  ]);

  return {
    absPreviewAsync: preview.absPreviewAsync as PinnedSurface['absPreviewAsync'],
    absApplyAsync: apply.absApplyAsync as PinnedSurface['absApplyAsync'],
    printJsonError: output.printJsonError as PinnedSurface['printJsonError'],
    printError: output.printError as PinnedSurface['printError'],
    printHint: output.printHint as PinnedSurface['printHint'],
    AuthError: authErr.AuthError as PinnedSurface['AuthError'],
    ProvisioningError: provErr.ProvisioningError as PinnedSurface['ProvisioningError'],
    armTenantInteractionRequiredError:
      absAdapter.armTenantInteractionRequiredError as PinnedSurface['armTenantInteractionRequiredError'],
    ConnectionMetadataStore:
      metaStore.ConnectionMetadataStore as PinnedSurface['ConnectionMetadataStore'],
  };
}

// ── argv → AbsCliOptions ───────────────────────────────────────────────────────────
//
// Maps the fixture argv to the option shape the pinned CLI produces from commander. The fixtures
// never embed --json; the runner sets `json` per output-mode run (human vs. machine-readable).

const VALUE_FLAGS: Record<string, string> = {
  '--tenant': 'tenant',
  '--subscription': 'subscription',
  '--resource-group': 'resourceGroup',
  '--location': 'location',
  '--endpoint': 'endpoint',
  '--bot-name': 'botName',
  '--display-name': 'displayName',
  '--sku': 'sku',
  '--identity-mode': 'identityMode',
  '--msa-app-id': 'msaAppId',
  '--app-object-id': 'appObjectId',
  '--deployment-id': 'deploymentId',
  '--account': 'account',
  '--resource': 'resource',
};

const BOOL_FLAGS: Record<string, string> = {
  '--force': 'force',
  '--non-interactive': 'nonInteractive',
};

interface ParsedInvocation {
  group: string;
  verb: string;
  options: Record<string, unknown>;
}

function parseInvocation(argv: string[], jsonMode: boolean): ParsedInvocation {
  if (argv.includes('--json')) {
    throw new Error(
      'Fixture argv must not embed --json; the runner drives both human and JSON output modes.'
    );
  }
  const group = argv[0];
  const verb = argv[1];
  const options: Record<string, unknown> = { json: jsonMode };
  for (let i = 2; i < argv.length; i++) {
    const token = argv[i];
    if (token in VALUE_FLAGS) {
      options[VALUE_FLAGS[token]] = argv[++i];
    } else if (token in BOOL_FLAGS) {
      options[BOOL_FLAGS[token]] = true;
    } else if (token === '--no-color') {
      // handled globally; no option field
    } else {
      throw new Error(`Fixture argv contains an unmapped token: ${token}`);
    }
  }
  return { group, verb, options };
}

// ── Fake Azure dependencies (the injected seam) ────────────────────────────────────

function buildReader(
  pinned: PinnedSurface,
  state: FixtureAzureState,
  targetTenant: string | undefined
): Record<string, unknown> {
  const graphReadGuard = (): never => {
    throw new Error(
      'Graph read must not happen on the scoped --msa-app-id (zero-Graph) path. The pinned core ' +
        'is expected to stub Graph reads before planning; reaching this fake is a regression.'
    );
  };
  // Faithful analogue of the pinned adapter's _acquireToken: when the silent ARM token for the
  // requested tenant needs interaction, every ARM read fails with the shared tenant-scoped exit-3
  // error (whichever read the core issues first surfaces it). Gated on a resolvable target tenant,
  // mirroring the adapter, which only reframes when `targetTenantId` is set.
  const armReadGuard = <T>(ok: () => T): (() => T) => {
    if (!state.targetTenantAuthFailure || targetTenant === undefined) return ok;
    return () => {
      throw pinned.armTenantInteractionRequiredError(targetTenant);
    };
  };
  return {
    getProviderRegistration: async () => armReadGuard(() => state.providerRegistration)(),
    getSubscription: async () => armReadGuard(() => state.subscription)(),
    getResourceGroup: async () => armReadGuard(() => state.resourceGroup)(),
    getBotService: async () => armReadGuard(() => state.existingBot ?? undefined)(),
    getApplicationById: async () => (state.graphReadsMustNotHappen ? graphReadGuard() : undefined),
    findApplicationsByDisplayName: async () =>
      state.graphReadsMustNotHappen ? graphReadGuard() : [],
  };
}

interface RecordingPorts {
  calls: string[];
  arm: Record<string, unknown>;
  graph: Record<string, unknown>;
}

function buildRecordingPorts(): RecordingPorts {
  const calls: string[] = [];
  const arm = {
    createOrUpdateBotService: async (p: { msaAppId: string }) => {
      calls.push('arm.upsert');
      return { botResourceId: 'fake-rid', provisioningState: 'Succeeded', msaAppId: p.msaAppId };
    },
  };
  const graph = {
    createApplication: async (p: { displayName: string }) => {
      calls.push('graph.createApp');
      return { objectId: 'fake-obj', appId: 'fake-app', displayName: p.displayName };
    },
    createServicePrincipal: async (appId: string) => {
      calls.push('graph.createSp');
      return { objectId: 'fake-sp', appId };
    },
    addCredential: async (p: { appObjectId: string }) => {
      calls.push('graph.addCred');
      return { kind: 'graphAppPassword', appObjectId: p.appObjectId, keyId: 'fake-key' };
    },
  };
  return { calls, arm, graph };
}

/** In-memory MetadataFileSystem so the real ConnectionMetadataStore exercises its no-secret write. */
function inMemoryFs(): unknown {
  const files = new Map<string, string>();
  return {
    read: async (p: string) => files.get(p),
    write: async (p: string, c: string) => {
      files.set(p, c);
    },
  };
}

function buildContext(
  pinned: PinnedSurface,
  fixture: Fixture,
  jsonMode: boolean,
  targetTenant: string | undefined
): { ctx: unknown; calls: string[] } {
  const { calls, arm, graph } = buildRecordingPorts();
  const state = fixture.azureState;
  const deps = {
    reader: buildReader(pinned, state, targetTenant),
    arm,
    graph,
    metadataStore: new pinned.ConnectionMetadataStore(inMemoryFs()),
    now: () => fixture.clock,
  };
  return { ctx: { deps, jsonMode }, calls };
}

// ── Output capture + normalization ─────────────────────────────────────────────────

const ANSI = /\x1B\[[0-9;]*m/g;

interface Captured {
  stdout: string[];
  stderr: string[];
}

async function captureConsole(fn: () => Promise<void>): Promise<Captured> {
  const stdout: string[] = [];
  const stderr: string[] = [];
  const toLine = (args: unknown[]): string => args.map((a) => String(a)).join(' ');
  const orig = { log: console.log, error: console.error, warn: console.warn };
  console.log = (...args: unknown[]): void => void stdout.push(toLine(args));
  console.error = (...args: unknown[]): void => void stderr.push(toLine(args));
  console.warn = (...args: unknown[]): void => void stderr.push(toLine(args));
  try {
    await fn();
  } finally {
    console.log = orig.log;
    console.error = orig.error;
    console.warn = orig.warn;
  }
  return { stdout, stderr };
}

/** Color/newline-normalized ordered semantic lines (ANSI stripped, trailing ws trimmed, blanks dropped). */
function normalizeLines(entries: string[]): string[] {
  return entries
    .join('\n')
    .replace(ANSI, '')
    .split(/\r?\n/)
    .map((l) => l.replace(/\s+$/, ''))
    .filter((l) => l.length > 0);
}

// ── Single invocation (mirrors pinned Cli.ts boot/run error→exit mapping) ──────────

interface InvocationResult {
  exitCode: number;
  threw: boolean;
  errorKind: string | null;
  captured: Captured;
  calls: string[];
}

async function runInvocation(
  pinned: PinnedSurface,
  fixture: Fixture,
  jsonMode: boolean
): Promise<InvocationResult> {
  const { group, verb, options } = parseInvocation(fixture.argv, jsonMode);
  const { ctx, calls } = buildContext(pinned, fixture, jsonMode, options.tenant as string | undefined);

  let exitCode = 0;
  let threw = false;
  let errorKind: string | null = null;

  const captured = await captureConsole(async () => {
    try {
      if (group !== 'abs') throw new Error(`Oracle only exercises the 'abs' group; got '${group}'.`);
      if (verb === 'preview') await pinned.absPreviewAsync(ctx, options);
      else if (verb === 'apply') await pinned.absApplyAsync(ctx, options);
      else throw new Error(`Oracle only exercises abs preview|apply; got '${verb}'.`);
    } catch (err) {
      // Mirrors boot() in the pinned src/Cli.ts: the shared R7 failure envelope. `errorKind` is
      // specific to ProvisioningError, but `remediation` is surfaced for ANY typed AuthError that
      // carries one (JSON field + stderr hint). Keep in sync with Cli.ts boot().
      threw = true;
      exitCode = err instanceof pinned.AuthError ? err.exitCode : 1;
      errorKind = err instanceof pinned.ProvisioningError ? err.kind : null;
      const message = err instanceof Error ? err.message : String(err);
      const remediation = err instanceof pinned.AuthError ? err.remediation : undefined;
      const details =
        errorKind !== null || remediation !== undefined
          ? {
              ...(errorKind !== null ? { errorKind } : {}),
              ...(remediation !== undefined ? { remediation } : {}),
            }
          : undefined;
      if (jsonMode) {
        pinned.printJsonError(message, exitCode, details);
      } else {
        pinned.printError(message);
        if (remediation !== undefined) pinned.printHint(remediation);
      }
    }
  });

  return { exitCode, threw, errorKind, captured, calls };
}

// ── Per-fixture: run human + JSON, cross-check, build actual result ────────────────

interface ActualResult {
  outcome: 'success' | 'error';
  exitCode: number;
  errorKind: string | null;
  mutationCalls: string[];
  golden: Golden;
}

async function runFixture(pinned: PinnedSurface, fixture: Fixture): Promise<ActualResult> {
  const human = await runInvocation(pinned, fixture, false);
  const machine = await runInvocation(pinned, fixture, true);

  // Output format must not change exit code, thrown-ness, error kind, or which mutations ran.
  if (human.exitCode !== machine.exitCode) {
    throw new Error(
      `Nondeterministic exit code across output modes: human=${human.exitCode} json=${machine.exitCode}.`
    );
  }
  if (human.threw !== machine.threw || human.errorKind !== machine.errorKind) {
    throw new Error('Thrown outcome/errorKind differs across output modes.');
  }
  if (!arraysEqual(human.calls, machine.calls)) {
    throw new Error(
      `Mutation calls differ across output modes: human=[${human.calls}] json=[${machine.calls}].`
    );
  }

  const jsonStdout = machine.captured.stdout.join('\n').trim();
  if (machine.captured.stderr.length > 0) {
    throw new Error(`JSON-mode run unexpectedly wrote to stderr: ${machine.captured.stderr.join('\n')}`);
  }
  let parsedJson: unknown;
  try {
    parsedJson = JSON.parse(jsonStdout);
  } catch {
    throw new Error(`JSON-mode stdout was not valid JSON:\n${jsonStdout}`);
  }

  return {
    outcome: human.threw ? 'error' : 'success',
    exitCode: human.exitCode,
    errorKind: human.errorKind,
    mutationCalls: human.calls,
    golden: {
      json: parsedJson,
      human: {
        stdout: normalizeLines(human.captured.stdout),
        stderr: normalizeLines(human.captured.stderr),
      },
    },
  };
}

// ── Comparison helpers ─────────────────────────────────────────────────────────────

function arraysEqual(a: string[], b: string[]): boolean {
  return a.length === b.length && a.every((v, i) => v === b[i]);
}

function canonical(value: unknown): string {
  const sort = (v: unknown): unknown => {
    if (Array.isArray(v)) return v.map(sort);
    if (v && typeof v === 'object') {
      return Object.keys(v as Record<string, unknown>)
        .sort()
        .reduce<Record<string, unknown>>((acc, k) => {
          acc[k] = sort((v as Record<string, unknown>)[k]);
          return acc;
        }, {});
    }
    return v;
  };
  return JSON.stringify(sort(value));
}

function deepEqual(a: unknown, b: unknown): boolean {
  return canonical(a) === canonical(b);
}

/** Verifies the hand-authored normative assertions against the live run. */
function checkAssertions(fixture: Fixture, actual: ActualResult): string[] {
  const problems: string[] = [];
  const a = fixture.assert;
  if (actual.outcome !== a.outcome) {
    problems.push(`outcome: expected ${a.outcome}, got ${actual.outcome}`);
  }
  if (actual.exitCode !== a.exitCode) {
    problems.push(`exitCode: expected ${a.exitCode}, got ${actual.exitCode}`);
  }
  if (!arraysEqual(actual.mutationCalls, a.mutationCalls)) {
    problems.push(`mutationCalls: expected [${a.mutationCalls}], got [${actual.mutationCalls}]`);
  }
  if (a.outcome === 'error') {
    const expectedKind = a.errorKind ?? null;
    if (actual.errorKind !== expectedKind) {
      problems.push(`errorKind: expected ${expectedKind}, got ${actual.errorKind}`);
    }
  }
  return problems;
}

/** Verifies the captured golden (exact JSON envelope + normalized human lines). */
function checkGolden(fixture: Fixture, actual: ActualResult): string[] {
  const problems: string[] = [];
  if (fixture.golden === null) {
    problems.push('golden is null — run the oracle with --update to capture it.');
    return problems;
  }
  if (!deepEqual(actual.golden.json, fixture.golden.json)) {
    problems.push(
      'json envelope mismatch:\n  expected: ' +
        JSON.stringify(fixture.golden.json) +
        '\n  actual:   ' +
        JSON.stringify(actual.golden.json)
    );
  }
  if (!arraysEqual(actual.golden.human.stdout, fixture.golden.human.stdout)) {
    problems.push(
      'human stdout mismatch:\n  expected:\n' +
        fixture.golden.human.stdout.map((l) => '    | ' + l).join('\n') +
        '\n  actual:\n' +
        actual.golden.human.stdout.map((l) => '    | ' + l).join('\n')
    );
  }
  if (!arraysEqual(actual.golden.human.stderr, fixture.golden.human.stderr)) {
    problems.push(
      'human stderr mismatch:\n  expected:\n' +
        fixture.golden.human.stderr.map((l) => '    | ' + l).join('\n') +
        '\n  actual:\n' +
        actual.golden.human.stderr.map((l) => '    | ' + l).join('\n')
    );
  }
  return problems;
}

// ── Main ───────────────────────────────────────────────────────────────────────────

function loadFixtures(): { path: string; fixture: Fixture }[] {
  const files = readdirSync(FIXTURES_DIR)
    .filter((f) => f.endsWith('.json'))
    .sort();
  return files.map((f) => {
    const path = join(FIXTURES_DIR, f);
    return { path, fixture: JSON.parse(readFileSync(path, 'utf8')) as Fixture };
  });
}

async function main(): Promise<void> {
  const args = parseCliArgs(process.argv.slice(2));

  // Force deterministic, non-TTY, no-color rendering regardless of the invoking terminal.
  process.env.NO_COLOR = '1';
  try {
    Object.defineProperty(process.stdout, 'isTTY', { value: false, configurable: true });
    Object.defineProperty(process.stderr, 'isTTY', { value: false, configurable: true });
  } catch {
    /* isTTY not overridable in this runtime; ANSI stripping + NO_COLOR still normalize output. */
  }

  const pinned = await loadPinnedSurface(args.pinnedDir);
  const fixtures = loadFixtures();

  console.log(`AgentHost ABS conformance oracle — ${args.update ? 'UPDATE' : 'VERIFY'}`);
  console.log(`  pinned worktree: ${args.pinnedDir}`);
  console.log(`  fixtures:        ${FIXTURES_DIR} (${fixtures.length})`);
  console.log('');

  let failures = 0;

  for (const { path, fixture } of fixtures) {
    let actual: ActualResult;
    try {
      actual = await runFixture(pinned, fixture);
    } catch (err) {
      failures++;
      console.log(`  FAIL  ${fixture.id} — runner error: ${(err as Error).message}`);
      continue;
    }

    const assertProblems = checkAssertions(fixture, actual);

    if (args.update) {
      if (assertProblems.length > 0) {
        failures++;
        console.log(
          `  FAIL  ${fixture.id} — live run disagrees with hand-authored assert; refusing to write golden:`
        );
        for (const p of assertProblems) console.log(`          - ${p}`);
        continue;
      }
      fixture.golden = actual.golden;
      writeFileSync(path, JSON.stringify(fixture, null, 2) + '\n', 'utf8');
      console.log(`  WROTE ${fixture.id} — golden captured (exit ${actual.exitCode})`);
      continue;
    }

    const goldenProblems = checkGolden(fixture, actual);
    const problems = [...assertProblems, ...goldenProblems];
    if (problems.length > 0) {
      failures++;
      console.log(`  FAIL  ${fixture.id}`);
      for (const p of problems) console.log(`          - ${p}`);
    } else {
      console.log(`  PASS  ${fixture.id} (exit ${actual.exitCode}, mutations [${actual.mutationCalls}])`);
    }
  }

  console.log('');
  if (failures > 0) {
    console.log(`RESULT: ${failures} of ${fixtures.length} fixture(s) failed.`);
    process.exit(1);
  }
  console.log(`RESULT: all ${fixtures.length} fixtures ${args.update ? 'captured' : 'passed'}.`);
}

await main();
