# Command telemetry controls

[Command reference](ReadMe.md)

The `msagent telemetry` commands manage a local remembered preference and report whether
collection is effective. They have no command-specific options; all accept the
[global options](ReadMe.md#global-options), including `--json`. These control commands do not
generate command telemetry themselves.

## Commands

| Command | Behavior |
| --- | --- |
| `msagent telemetry enable` | Persist an enabled preference and return the resulting policy state. Does not override an environment opt-out or unavailable activation. |
| `msagent telemetry disable` | Persist a disabled preference and return the resulting policy state. An explicit environment opt-in takes precedence. |
| `msagent telemetry status` | Read the remembered preference, environment override, activation availability, and effective state without changing the preference. |

```text
msagent telemetry status
msagent telemetry disable
msagent telemetry enable --json
```

The preference is stored in `telemetry/collection-preference.json` under the CLI's configuration
directory, not in the project's `agent.config.json`. Use these commands rather than editing that
file. A read or write failure is reported as a command error, not as a successful preference
change.

## Effective collection policy

`AH_CLI_TELEMETRY_OPTOUT` takes precedence over the remembered preference:

| Value | Effect |
| --- | --- |
| `1` or `true` | Disable collection regardless of the remembered preference. |
| `0` or `false` | Request collection regardless of the remembered preference, but only when activation is available. |
| Unset or empty | Use the remembered preference; if none exists, use the host's composition default. The packaged host defaults to enabled when activation is available. |
| Any other value | Disable collection with reason `invalid-environment-override`. |

The words `true` and `false` are case-insensitive. Values are not whitespace-trimmed.
An invalid remembered preference disables collection when no environment override decides the
policy. An enabled preference is not proof that an event was sent: collection activation and
normal per-invocation policy still apply.

Activation requires a supported public-cloud selection, a known ring, and a configured valid
ingestion key. Sovereign clouds do not fall back to public-cloud ingestion.

## Status output

Human output reports the preference, override, composition fallback, activation, and effective
collection state. With `--json`, the ordinary success envelope includes:

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Report schema version, currently integer `1`. |
| `status` | `status`, `preference-enabled`, or `preference-disabled`, depending on the command. |
| `rememberedPreference` | `not-set`, `enabled`, `disabled`, or `invalid`. |
| `environmentOverride` | `not-set`, `opted-out`, `opted-in`, or `invalid`. |
| `compositionFallbackEnabled` | Default policy used when no preference or override is set. |
| `collectionActivationAvailable` | Whether the host has an available collection destination/configuration. |
| `effectiveCollectionEnabled` | Whether the resolved policy allows collection. |
| `effectiveReason` | Reason such as `enabled`, `remembered-disabled`, `environment-opt-out`, `environment-opt-in`, or `collection-activation-unavailable`. |

## What command telemetry contains

Normal leaf dispatch can record an attempt and an outcome. Telemetry includes command identity
and bounded outcome metadata, not raw command arguments, queries, error messages, or stack traces.

Failure events can include `customDimensions.errorCode` as a string representation of the
numeric error code. `customDimensions.IsFailure` is a JSON boolean: true for error outcomes,
usage, sign-in-required, timeout, parser rejection, and group usage; false for success,
cancellation, help, version, and attempts. An attempt with `IsFailure: false` is not evidence of
a completed successful command. Both fields are inside the JSON-encoded `customDimensions`
object, not new top-level OneDS fields.
