# TrustVault frontend

Angular 21 client workspace. For setup, environment configuration, and the full
run instructions, see the [repository README](../README.md).

The initial Sprint 1 screen supports account registration and sign-in against
the API. Access tokens are held in memory only and are not persisted to browser
storage.

Run `npm ci`, `npm start`, `npm run lint`, and `npm test -- --watch=false
--browsers=ChromeHeadless` from this directory. The production API base URL is
configured in `src/environments/environment.production.ts`.
