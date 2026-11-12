# SharePoint schema (authoritative)

Read only for a SharePoint source, together with the common schema/naming and URL-normalization
contracts. Determine whether the normalized direct path identifies a file or a folder/library.

## Single file

```yaml
mcs.metadata:
  componentName: "Circle_of_fifths.docx"
  description: "This knowledge source provides information found in Circle_of_fifths.docx SharePoint."
kind: KnowledgeSourceConfiguration
source:
  kind: SharePointKnowledgeSource
  siteUrl: "https://contoso.sharepoint.com/sites/MySite/Shared%20Documents/Circle_of_fifths.docx"
  targetKind: File
```

A URL ending in a document (`.docx`, `.pdf`, `.pptx`, `.xlsx`, `.txt`, etc.) uses `targetKind: File`.

## Folder / library

```yaml
mcs.metadata:
  componentName: "Travel-Italy"
  description: "This knowledge source provides information found in the Travel-Italy SharePoint folder."
kind: KnowledgeSourceConfiguration
source:
  kind: SharePointKnowledgeSource
  siteUrl: "https://contoso.sharepoint.com/sites/MySite/Shared%20Documents/Travel-Italy"
  additionalSearchTerms:
  targetKind: Folder
```

A folder or document library uses `targetKind: Folder`. Include the empty
`additionalSearchTerms:` line for folder sources.
