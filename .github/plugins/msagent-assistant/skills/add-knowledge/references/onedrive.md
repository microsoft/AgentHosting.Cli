# OneDrive file schema (authoritative)

Read only for a OneDrive file, together with the common schema/naming and URL-normalization
contracts. OneDrive for Business is a personal SharePoint site, so use `SharePointKnowledgeSource`
with a `-my.sharepoint.*/personal/...` URL and `targetKind: File`.

```yaml
mcs.metadata:
  componentName: "popular_guitar_brands.docx"
  description: "This knowledge source provides information found in popular_guitar_brands.docx."
kind: KnowledgeSourceConfiguration
source:
  kind: SharePointKnowledgeSource
  siteUrl: "https://contoso-my.sharepoint.com/personal/user_contoso_onmicrosoft_com/Documents/popular_guitar_brands.docx"
  targetKind: File
```
