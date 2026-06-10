# CLAUDE.md — Arena Control

Microservice platform for organizing esports tournaments: teams, applications, brackets, live matches,
Discord notifications, MVP voting, prize pools, streams and analytics. UI text and API messages are in **Russian**.

## 1. Architecture Overview

Two applications behind one Nginx reverse proxy, all orchestrated by Docker Compose on a single bridge
network (`esports-network`). **The only public port is `:80` (nginx).**

```
Browser ──► nginx :80
              ├── /static/ , /media/   → shared Docker volumes (collected Django static, uploads)
              ├── /api/                → csharp-api:5000   (.NET 8 REST API, JSON only)
              ├── /hubs/               → csharp-api:5000   (SignalR WebSocket, Connection: upgrade)
              └── /  (catch-all)       → django-app:8000   (Gunicorn → Django SSR HTML)

django-app ──► http://csharp-api:5000/api   (server-to-server, internal Docker DNS)
csharp-api ──► ml-service:8001 (FastAPI, Elo-based match win predictions; internal-only, ML_SERVICE_URL + Enabled flag)
csharp-api ──► postgres:5432 (EF Core / Npgsql)
django-app ──► postgres:5432 (Django system tables only) + redis:6379 (django-redis cache)
csharp-api ──► PandaScore / Faceit / Liquipedia / Discord webhook / SMTP (external)
```

**Core interaction pattern — "Backend serves JSON, Django renders HTML":**

- The C# API (`backend_csharp/`) owns **all domain data and business logic**. Controllers return
  camelCase anonymous-object DTOs. There is no server-side HTML in the backend.
- Django (`frontend_django/`) is a **thin SSR frontend**. It holds *no domain models*
  (`core/models.py` is empty, no app migrations) — every page view in `core/views.py` calls the C# API
  through the singleton `api_client` (`core/api_client.py`, `CSharpApiClient`, `requests.Session`)
  and renders Django templates. Errors are mapped to a uniform `ApiResult(ok, data, error)` dataclass.
- The browser additionally talks **directly** to the backend through nginx: SignalR client connects to
  `/hubs/matches` for live score updates (`matchUpdated` event, groups `tournament:{id}` — see
  `Hubs/MatchesHub.cs` and the JS in `core/templates/match.html`).
- **Auth flow**: C# `AuthController` issues an *unsigned* JWT-shaped token (`alg: none`, suffix `.local`,
  8h exp). Django stores it in the session (`request.session["api_token"]`) plus a cached `current_user`,
  and forwards it as a `Bearer` header. The backend reads claims without signature verification via
  `Infrastructure/AuthTokenHelper.cs`. Roles: `viewer / player / captain / judge / admin`. Role checks are
  duplicated: in the backend (`AuthTokenHelper.IsInAnyRole`) and in Django for UI gating
  (`_role_flags()` in `core/views.py` — roles only count once email is verified).
- **Database schema** is created by EF Core `EnsureCreated()` at backend startup (there are no EF
  migrations) plus a raw `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` patch in `Program.cs`
  (`EnsureDbSchema`). `database/init.sql` is an **empty placeholder** mounted into the postgres container.
- **External tournaments**: `ExternalTournamentSyncService` lazily syncs PandaScore running/upcoming
  tournaments into the `Tournaments` table on every `GET /api/tournament` (throttled via `IMemoryCache`,
  10-min TTL). External tournaments (`IsExternal = true`) are **read-only**: no applications, no bracket
  generation, matches fetched live from PandaScore.

## 2. Stack & Versions

| Layer | Technology |
|---|---|
| Backend | C# / **.NET 8** (ASP.NET Core Web API + SignalR), `EsportsBackend.csproj` |
| Backend packages | EF Core 8.0.12, Npgsql.EntityFrameworkCore.PostgreSQL 8.0.11, Swashbuckle 6.5.0, MailKit 4.16.0, HtmlAgilityPack 1.11.59 |
| Frontend | Python 3.11 / **Django 4.2.7** (SSR, no DRF), Gunicorn 21.2.0 (4 workers), WhiteNoise 6.5.0, requests 2.31.0, django-redis 5.4.0, psycopg2-binary |
| Browser | Bootstrap **5.3.3** (CDN), `@microsoft/signalr` **8.0.7** (CDN), vanilla JS (`static/js/app.js`), custom CSS (`static/css/app.css`) |
| Data | PostgreSQL **15-alpine** (host port **5433** → 5432), Redis **7-alpine** (AOF, port 6379) |
| Proxy | Nginx **alpine** — upstream keepalive pools, gzip, WebSocket upgrade for `/hubs/` |
| External APIs | PandaScore (esports data), Faceit Open API v4 (player ELO linking), Liquipedia (stub), Discord webhooks, SMTP (Gmail + App Password, 587/STARTTLS) |

Configuration is environment-driven via root `.env` (see `.env.example`). Key vars:
`DJANGO_API_BASE_URL` (internal API URL), `PUBLIC_API_BASE_URL`/`PUBLIC_FRONTEND_URL` (browser-facing),
`PANDASCORE_TOKEN`, `FACEIT_API_KEY`, `DISCORD_WEBHOOK_URL`, `SMTP_*`, `DB_*`. Integrations degrade
gracefully when tokens are empty (services expose an `Enabled` flag; email falls back to logging the link).

## 3. Key Project Patterns

**Backend (`backend_csharp/`)**
- Controllers in `Controllers/` (route prefix `api/...`), business logic in `Services/`, single
  `Data/AppDbContext.cs`, POCO entities in `Models/`. DI registrations live in `Program.cs`.
- User-facing error messages are Russian; responses are `{ message = "..." }` objects or DTOs.
- `TournamentPlanningService` is the bracket engine: seeding by average player rating, classic
  power-of-two single-elimination tree built top-down (`Final` → `R1`) linked via `NextMatchId`,
  byes auto-advance; group stage uses snake seeding into groups A–D + round-robin matches.
- `MatchesController.SetMatchResult`: score ≥ 16 finishes a match (CS-style), winner advances into
  `NextMatch`; broadcasts `matchUpdated` to SignalR group and fires a Discord webhook on `live`.
- Typed `HttpClient`s per integration (`Program.cs`); `IMemoryCache` for API-call throttling/caching
  (Redis is *not* used by the backend). `LiquipediaRateLimitHandler` shows the project's
  delegating-handler rate-limit pattern (1 req / 2.1 s, static semaphore).
- `ITournamentProvider` (`FaceitTournamentService`, `LiquipediaService`) is a provider abstraction —
  registered but largely demo/stub level.
- Swagger is enabled only in `ASPNETCORE_ENVIRONMENT=Development`, and `/swagger` is **not** proxied by
  nginx (backend ports are not published to the host), so it's only reachable from inside the network.

**Frontend (`frontend_django/`)**
- Pages = function views in `core/views.py`; URLs in `esports_tournament/urls.py`. Forms POST back with a
  hidden `action` input (e.g. `create_tournament`, `approve_application`, `attach_stream`) — views
  dispatch on it, call the API, push a `django.contrib.messages` flash and redirect (PRG pattern).
- API payloads are defensively re-mapped via `_normalize_*` helpers (`_normalize_tournament`,
  `_normalize_match`, `_normalize_mvp_payload`) before reaching templates — never pass raw API dicts.
- Templates extend `core/templates/base.html`; `tournament_detail.html` is a Bootstrap tab hub
  (overview / bracket / applications / streams / prize / mvp, selected via `?tab=`). The
  `safe_json` filter (`core/templatetags/bracket_extras.py`) embeds JSON into scripts safely.
- **UI relies on Bootstrap 5 + custom CSS variables**: `app.css` defines a `:root` palette
  (`--bg`, `--surface`, `--accent: #c8a96a` gold, status colors); `app.js` duplicates the palettes and
  applies dark/light theme via `data-theme` / `data-bs-theme` attributes persisted in `localStorage`.
  `app.js` also handles favorites (localStorage), scroll-position restore on form submit, auto-dismiss
  alerts and IntersectionObserver entrance animations. Static files are served by nginx from the shared
  `static_volume` (collected with WhiteNoise manifest storage).
- Sessions (and Django admin) use Postgres via Django's built-in apps; Redis backs the Django cache.

**Conventions**
- JSON property names: camelCase on the wire; C# request DTOs are nested controller classes with
  DataAnnotations validation (400 shape customized in `Program.cs`).
- Statuses are lowercase strings everywhere: tournaments `planned|live|paused|finished`,
  applications `pending|approved|rejected`, matches `planned|live|finished`.
- Comments in code are mixed Russian/English — match the surrounding file.

## 4. Build & Run

```bash
# Full (re)build and start — the standard way
docker compose up -d --build

# Clean restart (wipes DB volume!)
docker compose down -v --remove-orphans && docker compose up -d --build

# Logs
docker compose logs -f csharp-api
docker compose logs -f django-app

# Smoke test (scripts/smoke.sh / smoke.ps1)
curl -fsS http://localhost/api/health && curl -fsS http://localhost/
```

- App: **http://localhost/** · API: **http://localhost/api/** · Health: **http://localhost/api/health**
- Postgres is reachable from the host at `localhost:5433` (`esports_user` / `esports123` / `esports_db`).
- Helper scripts in `scripts/` (`demo-up`, `demo-reset`, `smoke` — both `.sh` and `.ps1` variants).
- Startup order: postgres (healthy) → csharp-api (healthy, applies schema) → django-app
  (waits for pg, runs `migrate` + `collectstatic`, starts Gunicorn) → nginx (binds :80 immediately,
  serves 502 until upstreams warm up — by design).
- There are **no automated tests** in the repo; verification is manual via the README scenario list.

## 5. Gotchas (verified in code)

- `core/views.py` calls `api_client.generate_bracket(...)` (teams & tournament_detail views), but the
  client only defines `generate_tournament_bracket(...)` → the "generate bracket" form actions raise
  `AttributeError` at runtime. Keep naming consistent when touching either side.
- Empty placeholders: `database/init.sql`, `Controllers/RegistrationController.cs`,
  `core/models.py`, `core/admin.py`. (`Services/AnalyticsService.cs` is no longer empty — it holds
  the team win-rate logic for `GET /api/analytics/team-winrates`.)
- `Models/MvpVote.cs` and `Models/PrizePayout.cs` exist but have **no DbSet** in `AppDbContext` — MVP
  voting (`MvpController`) returns hardcoded demo data; prize distribution is stored as JSON in
  `Tournament.PrizeDistributionJson`, not in a payout table. `DemoOperationsController` (`api/demo/*`)
  is entirely in-memory demo data.
- Auth tokens are unsigned and passwords are plain SHA-256 — demo-grade security; do not reuse as-is.
- Schema changes require either an EF migration (none exist yet — `EnsureCreated` runs otherwise) or an
  addition to the raw-SQL patch in `Program.cs::EnsureDbSchema`. `EnsureCreated` does **not** update
  existing databases — that's exactly why `EnsureDbSchema` exists.
- `docker-compose.yml` healthchecks depend on `curl` being present in both app images (installed in the
  Dockerfiles) — keep it when editing them.
