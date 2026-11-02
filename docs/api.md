# API surface

All routes are protected by the API fallback authorization policy unless marked
anonymous. The browser's route guards are not an authorization boundary.

| Method | Route | Access | Purpose |
|---|---|---|---|
| POST | `/api/auth/register` | Anonymous; 10 requests/minute/IP | Create a Viewer account and return a short-lived access token |
| POST | `/api/auth/login` | Anonymous; 10 requests/minute/IP | Validate credentials and return a short-lived access token |
| GET | `/api/system/health` | Anonymous | Process health probe |
| GET | `/api/system/ready` | Any authenticated JWT | Readiness probe; returns 503 when PostgreSQL cannot be reached |
| GET | `/api/system/whoami` | Any authenticated JWT | Return the caller's subject and role claims |
| GET | `/swagger` | Anonymous, Development only | Development API documentation |

Registration creates accounts with the `Viewer` role; the request cannot select
or elevate roles. Passwords are validated and hashed by ASP.NET Core Identity.
Login returns the same `401` response for an unknown email, invalid password,
locked-out account, or suspended account. Repeated failed passwords trigger
Identity lockout. Access tokens default to 15 minutes; the configured lifetime
must remain between 5 and 30 minutes. Tokens are held only in Angular memory and
are lost when the page is reloaded or closed.

Refresh-token rotation, email verification, password reset, administrator user
management, and immediate revocation of already-issued tokens are not
implemented. `whoami` reads identity claims only and does not imply authorization
to any document. The in-memory IP limiter requires trusted forwarded-header
configuration when deployed behind a proxy; do not treat it as a substitute for
account-level monitoring or an edge rate limit.
