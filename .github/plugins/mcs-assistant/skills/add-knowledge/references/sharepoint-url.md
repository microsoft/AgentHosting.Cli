# SharePoint / OneDrive URL normalization

Read only for SharePoint/OneDrive sources, before writing. Copilot Studio needs a direct path,
not a browser UI URL. Require absolute HTTPS with no embedded username or password.

| URL pattern | Action |
|---|---|
| **Direct path** (`/sites/.../Shared%20Documents/...` or `/personal/.../Documents/...`) | **Use as-is**. |
| **`AllItems.aspx` with `?id=` param** | **Extract and decode** `id`, then prepend the origin (`https://<host>`) to build the direct path. Drop all query params (`?id=`, `&viewid=`, etc.). |
| **Sharing link** (`/:f:/s/...`, `/:w:/...`, `/:x:/...`, `/:b:/...`) | **Cannot convert**: the opaque token has no extractable path. Ask the user to open the item and copy its URL from the browser address bar. |

Spaces in the final `siteUrl` must be `%20` (e.g. `Shared%20Documents`).

Knowledge uses each end user's delegated permissions at runtime. Each user must have access to the
file/library, or the agent returns nothing for them. A file working for its author does not prove
that other users can access it.
