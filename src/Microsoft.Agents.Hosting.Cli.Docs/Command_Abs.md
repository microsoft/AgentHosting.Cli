# MSAgent CLI Azure Bot Service commands

[Back to command index](ReadMe.md)

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent abs preview`](#msagent-abs-preview) | Plan Azure Bot Service changes without mutation. |
| [`msagent abs apply`](#msagent-abs-apply) | Preview or apply Azure Bot Service changes. |

> **Gated.** The entire `abs` group is gated by `AzureBotServiceFeatures` and is hidden from `--help`
> (and rejected as unrecognized) unless that feature gate is enabled. Availability depends on the
> feature gates enabled for a given run; review `msagent --help` to see what is currently available.

`msagent abs preview` and `msagent abs apply` accept the same properties. The current C# implementation uses a
supplied App Registration and an ARM-only, zero-Graph provisioning path.

The shipped CLI targets Azure public-cloud ARM (`management.azure.com`); these commands expose
no sovereign-cloud or Power Platform API ring selector. Selecting an authentication profile with
`auth select` does not change the ABS ARM endpoint.

## `msagent abs preview`

Reads current Azure state and produces a change plan. It never mutates Azure, even if `--force` is
supplied.

```text
msagent abs preview --tenant <tenant-id> --subscription <subscription-id> \
  --resource-group <name> --location <region> --bot-name <name> \
  --endpoint <https-url> --msa-app-id <app-id> [options]
```

## `msagent abs apply`

Produces the same plan as preview. It mutates Azure only when `--force` is supplied; without
`--force`, it behaves as a preview. There is no confirmation prompt: `--force` is the explicit
confirmation, including under `--non-interactive`. It does not bypass conflicts or failed preflight
checks.

```text
msagent abs apply --tenant <tenant-id> --subscription <subscription-id> \
  --resource-group <name> --location <region> --bot-name <name> \
  --endpoint <https-url> --msa-app-id <app-id> [--force] [options]
```

## ABS properties

| Option | Value | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `--tenant` | String | Yes | None | Target tenant ID for the bot identity. Azure context is never inferred. |
| `--subscription` | String | Yes | None | Target Azure subscription ID. |
| `--resource-group` | String | Yes | None | Target Azure resource group. |
| `--location` | String | Yes | None | Azure region for the control-plane resource. Pass `global` unless the subscription is data-resident; it must be supplied explicitly. |
| `--bot-name` | String | Yes | None | Azure Bot Service resource name. Must be 2-64 characters, start with a letter or digit, and contain only letters, digits, hyphens, or underscores. |
| `--endpoint` | Absolute HTTPS URL | Yes | None | AgentHost/gateway messaging endpoint, normally ending in `/api/messages`. Loopback, placeholder, and `example.com` endpoints are rejected. The command does not probe reachability or enforce the path. |
| `--msa-app-id` | String | Yes | None | Existing Entra application/client ID connected to the bot through ARM. |
| `--display-name` | String | No | Bot name | Friendly bot display name. |
| `--sku` | `F0` or `S1` | No | `F0` | Azure Bot Service SKU. Values are normalized to uppercase. |
| `--identity-mode` | `ClientSecret` | No | `ClientSecret` | Bot identity mode. Other modes are not currently supported by this command. |
| `--app-object-id` | String | No | None | Entra application object ID intended to disambiguate an application name. It has no observable effect on the current supplied-app, zero-Graph review path. |
| `--deployment-id` | String | No | None | Accepted deployment ID. The current implementation does not include it in the report or persisted connection metadata and does not link a deployment. |
| `--force` | Boolean flag | No | `false` | On `apply`, confirms mutation. Without it, `apply` only previews. It does not make `preview` mutate. |

Although the generated help does not label the seven required ABS options as `(REQUIRED)`, runtime
validation requires `--tenant`, `--subscription`, `--resource-group`, `--location`, `--bot-name`,
`--endpoint`, and `--msa-app-id` for both commands.

Shortcuts are `-t` (tenant), `-sub` (subscription), `-rg` (resource group), `-loc` (location),
`-bn` (bot name), `-e` (endpoint), `-maid` (MSA app ID), `-disp` (display name), `-sku` (SKU),
`-im` (identity mode), `-aoid` (app object ID), `-did` (deployment ID), and `-f` (force).
`--identity-mode` is case-sensitive: use exactly `ClientSecret`.

## Access and preflight

Sign in before running either command. ARM token acquisition uses a separate consent-capable
client, trying cached credentials first and allowing broker/browser/device-code interaction if
needed. `--non-interactive` suppresses that interaction and fails when consent or sign-in is needed;
it does not turn a confirmed apply into a preview.

The target resource group must already exist, and `Microsoft.BotService` must be registered on
the subscription. Preflight reads the provider registration, subscription, resource group, and
existing bot. The operator needs read access at those scopes (for example Reader) and bot write
access to apply (for example Contributor). This command neither registers providers nor creates
resource groups or role assignments.

Preview is read-only, not offline: it performs real ARM requests and reports planned changes plus
`pass`, `fail`, or `unknown` preflight results. Review the report even if preview succeeds; a plan can
contain a conflict or failed check. Write permission, SKU capacity, and Azure Policy remain
`unknown` until a write is attempted, so a clean preview is not a guarantee that apply will succeed.

## Reconciliation and output

The bot is matched by its deterministic ARM resource ID (subscription, resource group, bot name).
Confirmed apply creates a missing bot, updates an existing bot when its messaging endpoint or SKU
differs, or returns `unchanged` without a PUT when those values already match. Display-name-only
or location-only differences do not trigger an update in the current planner.

Changing an existing bot's application ID or identity type is treated as a blocking conflict;
`--force` cannot rebind it. Choose another bot name or deliberately resolve the existing binding
outside this command, then preview again. `--app-object-id` does not resolve this ARM identity
conflict on the current zero-Graph path, despite generic remediation text mentioning it.

The upsert configures a `SingleTenant` bot with the supplied app ID and tenant. It does not verify
the application's existence or sign-in audience in Entra, create an application/service principal,
mint a client secret, or validate the messaging endpoint end to end. Arrange a compatible identity
and its runtime credentials separately. It also does not deploy an agent, configure channels or
OAuth connections, or register the bot with an AgentHost deployment.

The report includes Azure context, bot resource/app IDs, messaging endpoint, SKU, identity mode,
planned changes, preflight, and next actions. Status is `planned` for preview or apply without
force, and `created`, `updated`, or `unchanged` after confirmed apply. No credential material is
returned (`secretMaterialReturned` is `false`).

After a create/update, the CLI writes non-secret connection metadata to its local configuration
directory, scoped to the current Power Platform API ring. A no-op does not refresh that metadata.
ARM mutation and local persistence are not transactional: a local write failure can occur after
Azure has changed. Reinspect the bot rather than assuming a nonzero exit means no change occurred.
