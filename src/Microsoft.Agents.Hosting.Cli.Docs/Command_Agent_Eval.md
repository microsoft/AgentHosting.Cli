# MSAgent CLI evaluation commands

[Back to agent commands](Command_Agent.md)

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent agent eval run`](#msagent-agent-eval-run) | Run a Copilot Studio evaluation against an agent. |
| [`msagent agent eval list`](#msagent-agent-eval-list) | List an agent's evaluation runs, newest first. |
| [`msagent agent eval show`](#msagent-agent-eval-show) | Show one run and its per-conversation results. |
| [`msagent agent eval cancel`](#msagent-agent-eval-cancel) | Stop a run that is still queued or in progress. |
| [`msagent agent eval delete`](#msagent-agent-eval-delete) | Delete a finished run. |
| [`msagent agent eval generate`](#msagent-agent-eval-generate) | Generate conversations without creating a test set. |
| [`msagent agent eval testset list`](#msagent-agent-eval-testset-list) | List the test sets an agent holds. |
| [`msagent agent eval testset show`](#msagent-agent-eval-testset-show) | Show one test set and its conversations. |
| [`msagent agent eval testset create`](#msagent-agent-eval-testset-create) | Create a test set from a CSV or generated conversations. |
| [`msagent agent eval testset update`](#msagent-agent-eval-testset-update) | Add conversations to a test set, or replace the ones it holds. |
| [`msagent agent eval testset export`](#msagent-agent-eval-testset-export) | Write a test set's conversations out as CSV. |
| [`msagent agent eval testset delete`](#msagent-agent-eval-testset-delete) | Delete a test set and the conversations it holds. |

> **MCS-specific.** The entire `agent eval` group applies only to Microsoft Copilot Studio agents.
> It is gated by `EvaluationFeatures`. Availability depends on the feature
> gates enabled for a given run; review `msagent --help` to see what is currently available.

> [!IMPORTANT]
> `msagent agent eval run` consumes Copilot Credits. The cost is reported when the run finishes and
> is visible in the `CREDITS` column of `msagent agent eval list`, when supplied by the service.

Runs started here are the same runs the maker portal starts, and appear in the portal beside them.
The agent must already exist in Copilot Studio, and the signed-in account must be able to access
its environment and evaluation service. A local-only scaffold is not enough. The environment must
provide Dataverse access and a Copilot Studio agent-management endpoint. Standalone `MCSWorkflow`
records are not evaluation targets.

## Agent selectors

Every command in this group accepts the same four options to address one agent. They are omitted
from the per-command tables below.

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Enclosing project, else current directory | The project directory to act on. Discovery checks the current directory and up to five parents. |
| `--agent-id` | `-aid` | String | None | Remote bot ID or schema name, or a managed-agent record ID from the project. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | None | The agent to act on, by name. |
| `--environment-id` | `-eid` | String | The agent's own environment, else the active one | Act in this environment instead. |

Environment order:

1. `--environment-id`, when supplied.
2. The home environment recorded for the agent the selectors identify in this project. It is not
   automatically the most recently deployed environment or the environment of a promoted deployment.
   A stale `env select` cannot redirect a resolved project record, and that record needs no active
   environment. Use `--environment-id` to evaluate its copy in another environment.
3. The active environment, for an agent this project does not hold.

Resolution order:

1. `--agent-id`, resolved as a remote bot ID or schema name when the environment holds it.
   Otherwise, if the project directory has an agent configuration, the ID is checked against it;
   if not, the agent is reported as not found.
   An ID is never silently resolved to another agent.
2. The agent the project directory's configuration names (selected by `--agent-name`, or the only
   agent it holds). It must be an MCS agent.
3. Otherwise the environment's agents: `--agent-name` matches an agent's display name, schema name,
   or ID (case-insensitively), and with no name the environment must hold exactly one agent.

When several agents match, the command reports the ambiguity and asks for `--agent-id`.
For project-record resolution, an existing configuration is authoritative: a missing or ambiguous
record does not fall back to some other agent in the environment. To address a remote bot directly
from outside a project, supply both its `--agent-id` and `--environment-id`.

## Test set selectors

Options named `--testset` accept either the test set's display name or its component ID.
IDs take precedence over names, and both match case-insensitively after trimming the selector.
A name matching several test sets is rejected and the matching IDs are listed.

All command inputs here are named options; run IDs, test set selectors, and file paths are not
positional arguments.

## `msagent agent eval run`

Starts an evaluation. Either name a stored test set with `--testset`, or supply conversations with
`--from-csv` or `--generate` and name the test set the command creates with `--name`.

```text
# Run a stored test set and wait for the results
msagent agent eval run --testset <name-or-id> --watch

# Create a test set from a CSV and run it
msagent agent eval run --from-csv <file> --name <test-set-name> --watch

# Generate the conversations instead of supplying them
msagent agent eval run --generate 10 --turns 3 --name <test-set-name> --watch
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--testset` | `-tset` | String | No | None | An existing test set to run, by name or ID. Cannot be combined with `--from-csv` or `--generate`. |
| `--from-csv` | `-csv` | File path | No | None | A CSV of conversations, in the Copilot Studio template's format. Cannot be combined with `--generate`. |
| `--generate` | `-gen` | Whole number | No | None | Generate this many conversations from the agent's own description instead of reading a file. Must be positive. |
| `--turns` | `-trn` | Whole number | No | `3` | Turns per generated conversation. Must be positive. |
| `--name` | `-n` | String | Conditional | None | Names the test set created from `--from-csv` or `--generate`. Required with either. |
| `--update-testset` | `-uts` | Boolean flag | No | `false` | Replace the conversations in an existing test set of the same name instead of failing. |
| `--connection` | `-con` | String | No | None | The Copilot Studio connection ID the agent runs as. Passed to the evaluation service without a name lookup. Omit to evaluate without an authenticated profile. |
| `--published` | `-pubd` | Boolean flag | No | `false` | Evaluate the published agent instead of the draft. |
| `--watch` | `-w` | Boolean flag | No | `false` | Poll until the run finishes and print the results. |
| `--timeout-minutes` | `-tmo` | Whole number | No | `60` | How long `--watch` polls before giving up. Must be positive. |
| `--output` | `-o` | File path | No | None | Write the finished run to this file as JSON. Requires `--watch`. |

Supplying neither `--testset` nor a conversation source is a usage error, as is supplying both.
`--name` and `--update-testset` are used only when creating a test set from a conversation source;
they do not change a set selected by `--testset`. `--turns` is used only for generation, not CSV import.
New test sets use the service's general-quality grader; this command has no grader-selection option.

The test set created by `--from-csv` or `--generate` is durable and remains after the run. Reusing
its name fails unless `--update-testset` is supplied, which uses the same replacement safeguards as
[`testset update --replace`](#msagent-agent-eval-testset-update).

Without `--watch` the command returns as soon as the service accepts the run, without polling it; the
state it reports is whatever the service gives, usually `Queued`. A response that does not name the
run is refused rather than guessed at, because nothing could then show, stop or delete it.
With `--watch` it polls every five seconds, reporting progress as conversations are
scored. Reaching `--timeout-minutes` is not a failure: the run keeps going and the command reports
the run ID to read later with `msagent agent eval show`.

`--output` is rejected without `--watch`, because only `--watch` waits for the run that the file is
meant to contain. It always writes JSON, whatever the file is named, and writes nothing at all when
`--watch` gives up before the run finishes, since the file would otherwise hold a run still being
scored. To write a run that has already finished, use
[`msagent agent eval show --output`](#msagent-agent-eval-show), which chooses JSON or Markdown from
the file's name.

A test set holding no conversations is rejected before the run starts, so no credits are spent.

## `msagent agent eval list`

Lists the agent's evaluation runs, newest first.

```text
msagent agent eval list [--limit <number>]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--limit` | `-lim` | Whole number | No | `20` | Maximum rows to return. Must be positive. |

The output reports whether collection stopped at the limit.

## `msagent agent eval show`

Shows one run with its score, credits, and per-conversation grader results.

```text
msagent agent eval show --run-id <run-id> [--transcript] [--conversation <number>] [--output <file>]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--run-id` | `-rid` | String | Yes | None | The evaluation run to show. |
| `--transcript` | `-tr` | Boolean flag | No | `false` | Print every conversation's questions and answers below the results, shortened. |
| `--conversation` | `-conv` | Whole number | No | None | Print only this conversation's questions and answers, by its position in the results. Must be between 1 and the number of conversations the run holds. Takes precedence over `--transcript`. |
| `--output` | `-o` | File path | No | None | Write the full, untruncated run to this file. A `.json` name writes JSON; any other name writes Markdown. |

Transcripts printed to the terminal are shortened: questions are collapsed onto one line and
truncated, and replies are capped at six lines. When a reply was shortened the command says so and
points at `--output`, which always writes the full text.
`--conversation` limits transcript printing only; the results and exported file still contain all
conversations. The command can show or export a run still in progress, with results available so far.

## `msagent agent eval cancel`

Stops a run that is still queued or in progress. A run the service has already finished is rejected.

```text
msagent agent eval cancel --run-id <run-id>
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--run-id` | `-rid` | String | Yes | None | The evaluation run to stop. |

## `msagent agent eval delete`

Deletes a run that has already reached a terminal state. A run still in flight is rejected, and the
failure names `msagent agent eval cancel` as the command to run first. The test set the run used is
left alone. Terminal states include `Completed`, `Failed`, `Cancelled`, and `Deleted`; a reported
end time also qualifies. There is no confirmation prompt or `--force` option for run deletion.

```text
msagent agent eval delete --run-id <run-id>
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--run-id` | `-rid` | String | Yes | None | The evaluation run to delete. |

## `msagent agent eval generate`

Asks Copilot Studio to invent conversations from the agent's own description and lists them. No test
set is created and the agent's test sets are unchanged. Pass `--output` to keep a CSV copy. To store
those exact conversations, use
[`msagent agent eval testset create --from-csv`](#msagent-agent-eval-testset-create) with that file;
`testset create --generate` makes a fresh generation request instead.

```text
msagent agent eval generate [--count <number>] [--turns <number>] [--output <file>]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--count` | `-cnt` | Whole number | No | `10` | How many conversations to generate. Must be positive. |
| `--turns` | `-trn` | Whole number | No | `3` | Turns per conversation. Must be positive. |
| `--output` | `-o` | File path | No | None | Write the conversations to this file as CSV. Omit to list them without keeping a copy. |

A generation that returns nothing is reported as a failure rather than an empty result.
Generation requests use English (United States), locale `1033`; there is no locale option.

## `msagent agent eval testset list`

Lists the evaluation test sets an agent holds.

```text
msagent agent eval testset list
```

This command has no command-specific options beyond the [agent selectors](#agent-selectors).

## `msagent agent eval testset show`

Shows one test set and the conversations it holds.

```text
msagent agent eval testset show --testset <name-or-id>
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--testset` | `-tset` | String | Yes | None | The test set to show, by name or ID. |

## `msagent agent eval testset create`

Creates a durable test set from a CSV or from generated conversations.

```text
msagent agent eval testset create --name <name> --from-csv <file>
msagent agent eval testset create --name <name> --generate <number> [--turns <number>]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--name` | `-n` | String | Yes | None | The name to give the test set. |
| `--from-csv` | `-csv` | File path | No | None | A CSV of conversations. Cannot be combined with `--generate`. |
| `--generate` | `-gen` | Whole number | No | None | Generate this many conversations instead of reading a file. Must be positive. |
| `--turns` | `-trn` | Whole number | No | `3` | Turns per generated conversation. Must be positive. |

Exactly one conversation source is required. A name another test set already carries is rejected,
and the failure reports how many conversations that set holds. The name is matched against display
names only, so a name that reads like another set's ID still creates a new set.

## `msagent agent eval testset update`

Adds conversations to a test set, or replaces the ones it holds.

```text
# Append to what the set already holds
msagent agent eval testset update --testset <name-or-id> --from-csv <file>

# Leave the set holding only the new conversations
msagent agent eval testset update --testset <name-or-id> --from-csv <file> --replace
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--testset` | `-tset` | String | Yes | None | The test set to change, by name or ID. |
| `--from-csv` | `-csv` | File path | No | None | A CSV of conversations to add. Cannot be combined with `--generate`. |
| `--generate` | `-gen` | Whole number | No | None | Generate this many conversations instead of reading a file. Must be positive. |
| `--turns` | `-trn` | Whole number | No | `3` | Turns per generated conversation. Must be positive. |
| `--replace` | `-rep` | Boolean flag | No | `false` | Leave the set holding only the new conversations, discarding the ones it already held. |

Exactly one conversation source is required.

With `--replace`, the new conversations are normally stored before the old ones are removed, so a
refused write leaves the old conversations in place rather than emptying the set. A write the service
only partly accepts is reported, and the conversations it did accept are left alongside the old ones
for you to inspect when no space had to be freed.

If old and new conversations together would exceed 20, just enough old conversations are removed
first to make room. On a failed or partial addition, the command attempts to remove the partial
additions and restore what it removed; a failed restore explicitly reports the interrupted
replacement. Concurrent changes are detected before removing the remaining old conversations and
reported as a conflict, not silently deleted. Replacement is not an atomic service transaction.
`eval run --update-testset` uses this same behavior.

## `msagent agent eval testset export`

Writes a test set's conversations out as CSV, in the same format `--from-csv` reads.

```text
msagent agent eval testset export --testset <name-or-id> --output <file>
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--testset` | `-tset` | String | Yes | None | The test set to export, by name or ID. |
| `--output` | `-o` | File path | Yes | None | The CSV file to write. |

## `msagent agent eval testset delete`

Deletes a test set and the conversations it holds. Evaluation runs that used it are unaffected.

```text
msagent agent eval testset delete --testset <name-or-id> [--force]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--testset` | `-tset` | String | Yes | None | The test set to delete, by name or ID. |
| `--force` | `-f` | Boolean flag | No | `false` | Delete without asking for confirmation. |

The command asks the operator to type the test set's name before deleting. `--force` or the global
`--non-interactive` supplies that consent instead. A typed name that does not match reports
`name-mismatch` and deletes nothing; declining reports `cancelled`. Neither is an error.

## Conversation CSV format

`--from-csv` reads, and `testset export` writes, the CSV template the Copilot Studio portal uses.

```text
"conversationNumber","question","response"
"1","What is the best time of year to visit Rome?","Spring and autumn are mildest."
"1","How many days should I spend there?","Three to four days covers the main sights."
"2","How do I get from the airport to the city centre?",""
```

| Column | Required | Description |
| --- | --- | --- |
| `conversationNumber` | Yes | An integer that groups rows into one conversation. Turns within each group retain file order; groups are sorted by their original number and renumbered from 1 on import, so gaps are permitted. |
| `question` | Yes | The user prompt the agent responds to. |
| `response` | No | A reference answer stored with the conversation. The CLI does not compare replies itself; grading is performed by the service. An empty value means no reference answer. |

Column order is free and the header is matched case-insensitively. Rows whose first field starts
with `#` are treated as comments only before the header row, so a question may legitimately open
with `#`. A leading byte-order mark is accepted, as are both LF and CRLF line endings. Quoted
fields may hold commas, doubled quotes, and newlines.
Questions and reference responses are trimmed. Empty questions, a header without conversations,
and non-integer conversation numbers are rejected.

These are the limits the Copilot Studio import template states, and the CLI rejects a file that
exceeds them before writing anything. An append is checked against what the set already holds, so
filling a set and then adding to it is refused rather than silently overflowing:

| Limit | Value |
| --- | --- |
| Conversations per test set | 20 |
| Question-and-answer pairs per conversation | 6 |
| Characters per question | 1000 |

These limits also apply to generated conversations before storing them with `run`, `testset create`,
or `testset update`. The standalone `eval generate` command only checks that the requested count
and turns are positive and the response is non-empty; exporting a generation does not guarantee it
will fit a test set.
