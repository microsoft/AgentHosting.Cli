# Optional SharePoint / OneDrive access precheck

Read only after informed opt-in, before authentication. Verify that the signed-in author can read
the item without downloading it, using `GET /shares/{id}/driveItem`. Never block adding the source
on this optional, best-effort check.

## Consent and setup

Always explain that the result proves author access only. Knowledge retrieval uses each end user's
permissions, so remind the user to ensure end users have access.

Resolve `scripts/verify-knowledge-access.bundle.js` from this skill's directory as `<scriptPath>`.
Execute the bundle; do not read its source into context.

Treat every substituted argument as untrusted, including the helper path, agent directory, URL,
client ID, cloud, and output path. Use one single-quoted literal per value, for live checks,
help, and dry-run alike. Bash replaces each `'` with `'\''`; PowerShell doubles each single-quote
delimiter, including U+2018, U+2019, U+201A, and U+201B smart quotes. Reject NUL, line breaks,
and other control characters. Never use double quotes or command substitution for these values;
HTTPS validation and helper-side validation cannot prevent expansion before Node starts.

```bash
node '<scriptPath>' --agent-dir '<agentDir>' '<url>'
```

- Add `--dry-run` to resolve the encoded share id, Graph endpoint, scopes, and `needsClientId`
  **without** authenticating. Check setup first.
- Use `--help` for supported flags. For a live check, `--output '<new-file.json>'` retains the
  complete result in private session storage without overwriting. Stdout has a 12 KB budget;
  if `summaryOmitted` is true, read the full output before interpreting omitted fields.
  `--output` cannot be combined with the no-write `--dry-run`. Unknown flags, missing values
  and string-valued boolean switches are errors before authentication.
- Configure an independent Entra public-client app with `--client-id`, the
  `MCS_KNOWLEDGE_CLIENT_ID` environment variable, or the current plugin data directory's
  `knowledge-config.json` (`agents[agentId].appClientId` or
  `tenantDefaults[tenantId].appClientId`). No client ID is saved automatically;
  the retired `/chat` configuration is not read. The app must be owned by the
  user in the tenant, with delegated Graph `Files.ReadWrite` consented. Microsoft
  [documents this permission](https://learn.microsoft.com/graph/api/shares-get?view=graph-rest-1.0#permissions)
  as least-privileged for this endpoint. Although the script issues only a `GET`, consent grants
  read/write access to files the signed-in user can access. State that impact before opt-in.
  If `needsClientId` is true or setup/auth/permission errors occur, explain them and continue adding
  the source anyway.
- Tokens persist only when OS-backed encrypted storage is available. Otherwise use an in-memory
  cache for that run; never write Graph credentials to plaintext storage.
- US Government, DoD, and China SharePoint hosts automatically select corresponding Graph and Entra
  endpoints. `--cloud <name>` overrides inference when necessary.
- `AADSTS65002` means a Microsoft first-party/sample `--client-id` cannot obtain Graph tokens.
  Ask for the user's own single-tenant Entra app with public client flows enabled and delegated
  Graph `Files.ReadWrite` consented. An app working for Power Platform is not
  automatically valid for Graph. Report the setup issue and continue adding the source.

## Interpret the JSON result

| `status` | Meaning | Response |
|---|---|---|
| `ok` | Dry-run only; no request sent | Inspect `needsClientId`, `authority`, and `scopes` before authenticating. |
| `accessible` | Author can read the item | Proceed and repeat the end-user permissions caveat. |
| `forbidden` | Graph `/shares` returns 403 for both denied access and a link that does not resolve | Ask the user to re-copy the browser URL and confirm access for author and end users. Offer a retry; adding the source is still allowed. |
| `notfound` | Link did not resolve; uncommon because bad links usually return 403 | Suggest a wrong/renamed URL and ask for the browser address-bar URL. |
| `skipped` | Not SharePoint/OneDrive | No check needed. |
| `error` | Setup/auth/network problem | Surface the error; continue adding the source without a verified-access claim. |
