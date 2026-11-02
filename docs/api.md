# API surface (Sprint 0)

All routes are protected by the API fallback authorization policy unless marked
anonymous. The browser's route guards are not an authorization boundary.

| Method | Route | Access | Purpose |
|---|---|---|---|
| GET | `/api/system/health` | Anonymous | Process health probe |
| GET | `/api/system/ready` | Anonymous | Readiness probe; returns 503 when PostgreSQL cannot be reached |
| GET | `/api/system/whoami` | Any authenticated JWT | Return the caller's subject and role claims |
| GET | `/swagger` | Anonymous, Development only | Development API documentation |

No login, refresh, document, grant, audit, or administrator-management endpoint
is implemented yet. `whoami` reads identity claims only and does not imply
authorization to any document.
