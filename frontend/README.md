# TrustVault frontend

Angular 21 client workspace. For setup, environment configuration, and the full
run instructions, see the [repository README](../README.md).

The Angular screen supports administrator sign-in, member management, private PDF, TXT,
DOC, and DOCX upload/download, sharing, and an in-browser PDF editor for users
with owner or EDIT access. TXT has a direct text editor; Word text editing uses
the isolated converter service and saves simplified DOCX output without
preserving formatting or embedded media. The PDF page editor is PDF-only.
Access tokens are held in memory only and are not persisted to browser storage.
The PDF editor is loaded on demand so the main screen does not
pay its larger bundle cost until editing is opened.

Run `npm ci`, `npm start`, `npm run lint`, and `npm test -- --watch=false
--browsers=ChromeHeadless` from this directory. The production API base URL is
configured in `src/environments/environment.production.ts`.
