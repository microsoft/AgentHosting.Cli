# Uploaded-file schema (authoritative)

Read only for a local uploaded file, together with the common schema/naming contract.

Copy the actual file into `capabilities/knowledge/files/`, then write a sidecar **next to it** that
contains **only** the `mcs.metadata` block (no `kind:`/`source:`):

```yaml
mcs.metadata:
  componentName: "electric-guitar-history.md"
  description: "This knowledge source searches information contained in electric-guitar-history.md"
```

- `componentName` is the file name with extension.
- Per Copilot Studio's
  [uploaded-document requirements](https://learn.microsoft.com/microsoft-copilot-studio/knowledge-add-file-upload#supported-document-types),
  supported extensions are `.doc`, `.docx`, `.xls`, `.xlsx`, `.ppt`, `.pptx`, `.pdf`, `.txt`,
  `.md`, `.log`, `.html`, `.htm`, `.csv`, `.xml`, `.odt`, `.ods`, `.odp`, `.epub`, `.rtf`,
  `.pages`, `.key`, `.numbers`, `.json`, `.yml`, `.yaml`, and `.tex` (case-insensitive).
- Reject files larger than 512 MB, encrypted/password-protected files, images, video, executables,
  audio, and other unsupported types. PDFs should contain selectable text or annotated images.
- The source must be a regular file of a supported type. Do not copy directories or unsupported
  files, and do **not** create a sidecar for a file that is not physically present.
- Before copying, check whether the destination payload exists and scan sidecars for a matching
  `mcs.metadata.componentName`. Never overwrite either one without explicit confirmation.
- For an explicit replacement, preserve and update the one existing sidecar filename. If no matching
  sidecar exists or several match, stop rather than guessing. To keep both sources, choose a
  different destination payload name and use that exact name as `componentName`.
