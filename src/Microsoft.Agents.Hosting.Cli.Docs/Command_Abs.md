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
`--force`, it behaves as a preview.

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
| `--location` | String | Yes | None | Azure region for the control-plane resource. |
| `--bot-name` | String | Yes | None | Azure Bot Service resource name. Must be 2-64 characters, start with a letter or digit, and contain only letters, digits, hyphens, or underscores. |
| `--endpoint` | Absolute HTTPS URL | Yes | None | Publicly reachable AgentHost/gateway `/api/messages` endpoint. Loopback, placeholder, and `example.com` endpoints are rejected. |
| `--msa-app-id` | String | Yes | None | Existing Entra application/client ID connected to the bot through ARM. |
| `--display-name` | String | No | Bot name | Friendly bot display name. |
| `--sku` | `F0` or `S1` | No | `F0` | Azure Bot Service SKU. Values are normalized to uppercase. |
| `--identity-mode` | `ClientSecret` | No | `ClientSecret` | Bot identity mode. Other modes are not currently supported by this command. |
| `--app-object-id` | String | No | None | Entra application object ID intended to disambiguate an application name. It has no observable effect on the current supplied-app, zero-Graph review path. |
| `--deployment-id` | String | No | None | AgentHost deployment ID to associate with the connection metadata. |
| `--force` | Boolean flag | No | `false` | On `apply`, confirms mutation. Without it, `apply` only previews. It does not make `preview` mutate. |

Although the generated help does not label the seven required ABS options as `(REQUIRED)`, runtime
validation requires `--tenant`, `--subscription`, `--resource-group`, `--location`, `--bot-name`,
`--endpoint`, and `--msa-app-id` for both commands.
