# Rig2Cast user management plan

## Security model

Rig2Cast will use authenticated users with direct permission grants. The existing browser client ID and
Operator/Observer attachment remain responsible for radio-session arbitration; they are not authentication.
Authorization is enforced by the web server at every HTTP and WebSocket entry point. The browser only uses
the effective permission set to present cards and read-only controls clearly.

The default is deny. Station-wide configuration remains an upper safety limit: a user permission can never
enable an operation disabled by `station.json`.

## Permission granularity

Each functional card has `view` and `control` permissions. Card control covers ordinary writes, while sensitive
operations require an additional explicit permission:

| Area | Card permissions | Explicit sensitive permissions |
| --- | --- | --- |
| Radio/VFO | `radio.view`, `radio.control` | `radio.connect`, `radio.preempt` |
| Spectrum | `spectrum.view`, `spectrum.control` | `spectrum.settings` |
| Tuning | `tuning.view`, `tuning.control` | — |
| Band/mode | `band.view/control`, `mode.view/control` | — |
| DSP/receiver | `dsp.view/control`, `receiver.view/control` | — |
| Transmit | `transmit.view`, `transmit.control` | `transmit.power`, `transmit.ptt` |
| Audio | `audio.view`, `audio.control` | `audio.transmit` |
| Physical radio | state visible with radio access | `radio.power.on`, `radio.power.off` |
| Rotators | `rotator.view`, `rotator.control` | `rotator.move`, `rotator.stop`, `rotator.park` |
| Administration | — | `users.manage`, `audit.view` |

Rotator permissions can carry a resource ID. For example, a grant for `rotator.move` and resource
`main-rotator` does not authorize any other rotator. A null resource applies to all resources in that area.

The operational station session is exclusive. One authenticated browser owns radio, spectrum, audio, and
rotator writes at a time. `radio.preempt` permits an explicitly confirmed transfer; it is intentionally separate
from `users.manage` so user administrators do not automatically gain operational takeover authority.

## Persistence

SQLite stores users, direct allow/deny grants, revocable sessions, audit events, and a schema version. Passwords
are stored only as adaptive hashes. Session secrets are sent in secure HTTP-only cookies; only their hashes are
stored in the database.

The database schema is initialized automatically, but user management is feature-flagged. Enabling it will
require an explicit administrator bootstrap procedure so there is no default account or default password.

## Delivery slices

1. **Foundation (complete):** permission catalogue, SQLite schema/versioning, configuration, and persistence tests.
2. **Authentication (complete):** one-time administrator bootstrap, password hashing, login/logout, secure cookie sessions,
   expiry and revocation.
3. **Server authorization (complete):** protect all HTTP/WebSocket endpoints, including sensitive-control and
   per-rotator checks. Denied checks are written to the security audit log.
4. **Permission-aware GUI (in progress):** login and forced password-change screens, card visibility, initial
   read-only presentation, and sensitive-control checks are complete. Fine-grained generated-control mapping
   and per-resource rotator presentation remain.
5. **Administration GUI (in progress):** create/disable/delete users, reset passwords, and edit global direct
   grants are complete. Resource-scoped grant editing, session revocation, and audit history remain.
6. **Hardening:** CSRF protection, security headers, backup/restore documentation, database permission checks,
   and end-to-end authorization tests.

Login throttling remains part of the authorization/hardening pass before user management is enabled in production.

## Administrator bootstrap

Bootstrap has no default credentials and runs only when the enabled database has no users. Set
`UserManagement.BootstrapAdminUsername` in the station configuration and provide the initial password through
the process environment variable `RIG2CAST_BOOTSTRAP_ADMIN_PASSWORD` for one startup. The password must contain
at least 12 characters. The server creates the administrator with all current grants, marks the password for
change, clears the process environment variable, and will never bootstrap again once a user exists.

Remove `BootstrapAdminUsername` and the environment variable immediately after the first successful startup.

## Compatibility during implementation

When user management is disabled, current station behavior remains unchanged. When enabled, unauthenticated
requests are denied except for login and minimal public station identity. This compatibility mode is temporary
scaffolding, not an authorization fallback after the feature is enabled.
