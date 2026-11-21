# Copilot Studio chat canvas

A GitHub Copilot app canvas, contributed by the `msagent-assistant` plugin, backed by `msagent test ask --json`.
It does not embed Playground or start a separate chat browser/terminal.

## Requirements

- GitHub Copilot app/runtime with extension canvases enabled. This SDK surface is
  experimental; unsupported hosts cannot render the pane.
- Node.js 22 or newer and an installed msagent executable with
  `test ask --start-conversation` and `--file`. This change adds both options;
  there is no claimed released minimum CLI version.
- The .NET and ASP.NET Core runtimes required by that CLI build.
- A selected public-cloud, production-ring account with access to the agent.
  The canvas never signs in; it reuses the CLI account from `msagent auth create`.

The extension does not install dependencies, upgrade msagent, enable feature
flags, create app registrations, deploy agents, or publish anything.

## Open

The app loads this extension from the installed `msagent-assistant` plugin, which
declares `"extensions": "extensions/"`. Reinstall the plugin and reload extensions
after changes. Opening **Copilot Studio chat** from the canvas menu without
input shows a setup screen; it does not check credentials, run the CLI, or
start a live conversation. **Choose an agent with Copilot** sends a request to
the main Copilot chat. Sign-in and agent selection happen there, then Copilot
configures this same canvas with the selected target and friendly agent name.

When asked to open a chat, Copilot helps select the agent and deployment in the
main chat first, then opens a configured canvas with that target and its friendly
name. It does not open an empty setup canvas for a chat request. The canvas type
ID is `copilot-studio-chat`.

Registered target input:

```json
{
  "chatId": "my-agent-dev",
  "target": {
    "project": "/absolute/path/to/registered/project",
    "agentId": "local-managed-agent-id",
    "deploymentId": "local-deployment-id"
  }
}
```

For Windows use an absolute Windows project path. Alternatively, supply a
complete explicit target with no registration:

```json
{
  "chatId": "explicit-agent-dev",
  "target": {
    "schemaName": "agent_schema",
    "environmentId": "environment-id",
    "tenantId": "11111111-1111-1111-1111-111111111111",
    "deploymentName": "development"
  }
}
```

An explicit deployment name is only a descriptive label, not a verified
registry association. Local managed-agent IDs are not cloud MCS IDs; the canvas
does not accept a cloud MCS ID as an alternative selector.

Supply `agentName` with the selected agent's display name for assistant bubble
headings. Without it, headings use the target's schema name or local agent ID.
This label does not change the selected target.

Optionally add `executablePath` with the absolute path to the compatible
msagent native executable. On Windows, discovery looks for the current .NET 10
`msagent.cmd` launcher in absolute PATH entries, reads only the standard forwarding
template, and starts its exact tool-store `.exe` with no shell. An absolute path
to that launcher is also accepted. Arbitrary batch scripts, legacy launcher
layouts and guessed tool-store versions are not supported. If the first discovered
launcher is invalid or its target is missing, fix that installation or supply
the native executable path; another installation is not silently selected.
On macOS/Linux, discovery still invokes the `msagent` PATH entry, including
symlinks, without resolving it to a different executable path. For a local
contributor build, supply the apphost under
`src/Microsoft.Agents.Hosting.Cli/bin/Debug/net10.0/`
(`Microsoft.Agents.Hosting.Cli.exe` on Windows).
Windows native invocation uses an extended-length executable path so deeply
nested tool installations can start; query and file arguments remain literal.

Each canvas has its own conversation and transcript, even if another canvas uses
the same `chatId`. Reopening an existing panel reuses its conversation. Closing a
panel ends its backend and discards its transcript.

## Use

Copilot opens a configured canvas for an agent it selected. Opening checks command
capabilities, local registry association and auth status; it fails if the CLI
is signed out, so Copilot can run `msagent auth create` and reopen it. Opening
never signs in. The compact header shows the selected agent. Open
**Connection details** (the info icon) for the CLI account, agent, deployment,
environment ID and tenant ID. Click outside the panel to close it.
The canvas reuses the CLI's account; it does not handle Entra tokens or run
`auth create`. A signed-out or unavailable connection is shown in the header.
Sign in with `msagent auth create`; the canvas checks when it opens and when
starting a new chat, and rechecks on focus only while it shows an error. Sending
runs only `test ask`, so the message appears at once; a sign-in failure is
reported from that call. Sending does not recheck the CLI account, so switching
`msagent` accounts during a conversation is not supported; start a new chat
after switching. CLI
authentication is unchanged.

Once the checks pass, opening a new canvas automatically starts a live
conversation. This runs `test ask --start-conversation --json --non-interactive`
once, displays any opening activities and retains the returned conversation ID.
It does not send a synthetic user message. Starting a conversation may itself
run configured topics/actions, use credits, and an agent is not required to
provide a greeting. Reopening an open canvas reuses its conversation. Each open
canvas saves its conversation ID, transcript, Debug data, target and CLI account under
`$COPILOT_HOME/extensions/copilot-studio-chat/artifacts/started/` (owner-only
file permissions where supported). When the extension or app restarts, the
runtime reopens existing canvases and each resumes its own conversation with `--conversation-id`
without a live call. If the CLI account or resolved target changed, or the last
turn's outcome was uncertain, the canvas shows the saved transcript with a
single recovery card with its own **Start new chat** button; a changed target
is reported only as "Something went wrong." The header **New chat** button stays
available. The composer is disabled until a new chat starts; no message is replayed.
Before contacting the agent, the canvas saves an uncertain-turn marker so an
interrupted process cannot restore the previous conversation as if the turn had
not happened. If that safety record cannot be saved, the agent is not contacted;
later save failures are shown without marking a delivered message as unsent.
Files returned before a restart show their names but must be requested again.
Closing a canvas deletes its saved conversation, so opening it again starts a
new one. Saved conversations of panels closed while the extension wasn't
running, or of deleted sessions, are deleted when the extension starts once
they're unused for 30 days.
**New chat** discards the current conversation (or a failed or uncertain one) in
that canvas only and immediately starts a fresh one. New chat and Send are live
actions that may use credits or change data.

Each message
starts a background `test ask --json --non-interactive` process. Continuations
use only the returned conversation ID with the same target. The extension
rechecks registry metadata and selected account around turns; do not switch CLI
accounts concurrently with a running turn. An observed identity/target change
stops continuation.

Press **Enter** to send, or **Shift+Enter** for a new line. Sending clears the
composer immediately. A failed send restores the draft only if it has not been
edited since submission. A typing indicator appears immediately when connecting
or sending and clears on completion, cancellation or failure, not during local
status checks.

Replies update while the CLI is running: complete activities are parsed
incrementally from its single JSON document, and the pane refreshes every
100 ms during a turn. Copilot Studio `chunkType: delta` fragments are appended,
including whitespace and the initial fragment before its stream ID arrives;
`replace` snapshots and final messages replace earlier text for that stream.
Stale updates and duplicate finals are ignored. Update
granularity depends on what Copilot Studio and the SDK emit, not individual
tokens. Informative/diagnostic events and unknown delta payloads are not rendered.
The final JSON result and conversation metadata are validated before the reply
is marked complete. Interrupted replies retain their text marked incomplete,
and streams without a final snapshot remain marked incomplete. Interactive cards
and unsupported attachments receive a notice; interactive submissions and connector
sign-in are not supported.

Assistant replies render a safe Markdown subset while updating: headings,
paragraphs, emphasis, inline/fenced code, simple lists, blockquotes, rules and
pipe tables. Raw HTML is displayed literally; images are replaced by their alt
text without loading them. Only explicit HTTP(S) links are clickable, opening
on user interaction with no referrer. Other URL schemes remain literal text.
This is not a full CommonMark/GFM implementation; nested lists and advanced
extensions are not supported. Replies over 100,000 characters or 2,000 rendered
elements fall back visibly to full plain text. No external renderer is loaded.

Closing the canvas stops the local invocation, not remote actions already
delivered. If we couldn't confirm whether the agent received the last message,
Chat shows a recovery card; the detailed CLI result remains in Debug. Start a
new chat to continue. No message is retried automatically, including after
sign-in. If a connected chat reports that a message was not attempted
because sign-in or consent is needed, sign in again, then send the restored
draft again.
An observed account change also requires a new conversation. A changed
registered target requires reopening with a new chat ID.

The pane retains drafts on failure. Transcripts survive extension/app restarts
(see above) and are deleted when the panel closes. Refreshing the
iframe while its backend lives preserves the chat. The limits are 16,000 input
characters, 100 transcript entries, 1 MB retained transcript, 4 MiB combined CLI
output for setup and status checks, 16 MiB combined CLI output for live turns,
and a two-minute operation timeout. Sending requires space for both the user
message and the agent reply; an opening greeting also counts toward the entry limit.

### Conversation data (JSON)

Use the **Chat / Debug** switch to choose between the rendered conversation and
raw response data; they are separate views of the same session. The message
composer is hidden in Debug; switch back to Chat to send.
Chat is selected by default. Debug is an alternate representation of the same
conversation, not a separate recorder: messages and responses already received
in Chat are immediately available in Debug. Switching views is presentation-only
and works during streaming; it never sends a message or restarts the conversation.

Use **Find in debug data** to search the displayed JSON, ignoring case. The current
match is highlighted; use **Previous** and **Next**, or Shift+Enter and Enter, to
move through matches. Results update as the conversation data changes.
Search includes hidden streaming chunks and temporarily reveals only the group
containing the selected match. Moving to a match elsewhere, clearing the search,
or **Collapse chunks** hides it again.

Each bubble has an ellipsis menu with **View debug**. It switches views and
highlights that turn's raw response for an agent bubble, or the input JSON for a
user bubble. The highlight covers the whole entry, including its streaming chunks. Stable turn IDs keep the link correct through streaming and repeated
text. If older raw response details were discarded, the corresponding transcript
entry is highlighted instead and the omitted-data notice remains visible.
Returning to Chat clears this message highlight; opening Debug normally does
not select the previous message again.

The debug view includes the current transcript and responses from conversation starts and
subsequent sends. Each response shows activities as they arrive, including
informative/tool events omitted from chat, followed by the final CLI JSON envelope.
Consecutive streaming chunks are hidden by default behind a single
**N streaming chunks hidden** line. Use **Expand chunks** / **Collapse chunks**
beside the search box to show or hide all groups, including groups that arrive
later; the button offers Collapse whenever any chunks are visible. Expanded groups render their recorded chunks in their original order; final
messages and other events remain visible. Collapsing does not discard data or
change the existing retention limit.
It shows only what `ask` emits; hidden reasoning or tool
details not returned by the service are not available.

Raw response history is capped at 1 MB and saved with the conversation after
each turn, so Debug survives extension and app restarts. A turn that is
interrupted by a restart keeps the chunks received before the provider stopped
and is labeled incomplete. Older raw responses are discarded first when needed; oversized
responses retain a bounded prefix. Incomplete and omitted response details are
explicitly labeled without removing messages from the chat transcript.
New chat clears both representations.
Activities and the final CLI envelope are shown verbatim, without masking; they
may contain sensitive agent or business data. Raw HTTP traffic is not part of
the CLI output. Auth results and CLI stderr
are never retained in conversation data. Payloads are displayed as inert JSON text, never Markdown
or executable content, and are not written to logs or exposed by canvas actions.

### Files

Use the paperclip icon in the ask box (**Attach files**) to select up to five files,
at most 10 MiB total. Selection stays local until you select **Send** with a
nonblank message. Files can be removed
before sending. The extension stages private temporary files, passes only their
paths to `test ask --file`, and deletes them after the invocation, including
failures and cancellation. The transcript retains names and sizes, not upload bytes.
Windows device names (including console devices and numbered-device aliases),
path separators and trailing dots/spaces are rejected on every platform.
The installed CLI must support `--file`; the agent controls which file types it accepts.

File attachments returned by the agent have **Download** buttons. Downloads use
the protected local gateway and are never fetched automatically. Inline base64
files and public HTTPS URLs are supported, with a 10 MiB download limit, bounded
redirects, and no CLI credentials forwarded. Private/loopback destinations and
links requiring an additional login are not supported. Expired or unsupported
files report an error rather than retrying the agent. Inline files also count
toward the 16 MiB combined live-turn output limit, including any JSON escaping
and other activities.

Download references are held only in memory with a URL/data budget large enough
for one base64-encoded 10 MiB file and 4 KiB of overhead; older references can
expire when the limit is reached. New chat clears them. Files are saved as
attachments, never embedded or executed.

## Privacy and local access

Microsoft tokens stay in the CLI and its normal secure cache. The renderer and
extension never request token output. Auth errors are reduced to safe messages;
stderr and raw auth results are not exposed through canvas actions. The actions
report status, configure an unconfigured panel, send a message as the user
(`send_message`) and read the conversation (`get_transcript`). A message Copilot
sends appears in the panel like a typed one and is a live call to the agent.
`send_message` returns once the message is sent; when the agent finishes
replying, the extension starts a Copilot turn with the reply, marked as untrusted
agent output. Replies to messages typed in the panel do not notify Copilot.
`get_transcript` returns the role, text, partial flag and file names of every
message, including those typed in the panel; it does not return Debug data or
file contents. `new_chat` discards the conversation like the New chat button;
its description tells Copilot to use it only when the user asks for a new chat
or the current conversation must be restarted.

Messages are passed as literal CLI arguments, not shell commands. Arguments may
be visible to local process inspection; do not paste credentials. Treat agent
replies as untrusted text. Links are not auto-opened and HTML is not executed.
Chat invokes real services and can consume credits or change data.

The loopback server requires a random capability for data/actions, checks
Host/Origin, does not enable CORS, and serves a restrictive CSP. Inline styles
are allowed only so the app can inject its theme; the page builds agent content
with DOM text nodes, never HTML strings. The capability
in the canvas URL fragment is local authorization, not a Microsoft token.
Closing the pane stops owned children and closes the listener.

## Development

No npm dependencies or build step are needed:

```text
npm --prefix .github/plugins/msagent-assistant/scripts test
```

Tests use fake CLI results and local Node subprocesses, not cached credentials
or live agents. Windows and Linux CI already run the plugin suite; macOS CI
also runs the canvas tests. Windows isolated installed-package acceptance uses
the actual built .NET tool's launcher for canvas discovery and `test ask --help`
capability checks, without sign-in or starting a conversation. Launcher fixtures
cover spaces, executable paths over 260 characters, literal arguments,
unsupported templates and missing targets;
the Unix symlink fixture does not require Windows symlink privileges.
Reloading extensions resumes the conversations in open
canvases. Discovery, capability and status checks are read-only; opening a new
configured canvas starts a live conversation. Live acceptance requires explicit
target/effects approval and a record following `docs/developer/ManualTesting.md`;
no cross-platform live compatibility is claimed by the offline suite.

This extension is distributed as part of the `msagent-assistant` plugin and
requires a GitHub Copilot host with canvas support.
