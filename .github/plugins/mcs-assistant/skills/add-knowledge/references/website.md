# Public website schema (authoritative)

Read only for a public website source, together with the common schema/naming contract.

```yaml
mcs.metadata:
  componentName: "https://en.wikipedia.org/wiki/Electric_guitar"
  description: "This knowledge source searches information on the web found in https://en.wikipedia.org/wiki/Electric_guitar website"
kind: KnowledgeSourceConfiguration
source:
  kind: WebsiteKnowledgeSource
  siteUrl: "https://en.wikipedia.org/wiki/Electric_guitar"
```

- No `targetKind`.
- The URL defines a **search scope**. Following Copilot Studio's
  [public-website URL rules](https://learn.microsoft.com/microsoft-copilot-studio/knowledge-add-public-website#url-type-and-structure),
  it must be an absolute HTTPS URL with no embedded username or password and at most **two nonempty
  path segments** after the host (ignore one trailing slash). Reject deeper paths and ask for a
  broader base URL.
- The site must be publicly accessible without a login and indexed by Bing. Do not use a search
  engine URL as a source.
