# Аудит учебного проекта: «Система для организации киберспортивных турниров»

> Цель аудита: показать текущее состояние проекта и дать минимальный, реалистичный план доработок для стабильного локального демо (docker-compose), без production-практик.

## A) Карта проекта

### Контейнеры и зависимости (`docker-compose`)
- `csharp-api` (ASP.NET Core 8 + EF Core + PostgreSQL)
  - Порт: `5000:5000`
  - Зависит от `postgres` (healthcheck)
  - Healthcheck: `GET /api/health`
- `django-app` (Django 4.2 фронт + интеграция с C# API)
  - Порт: `8000:8000`
  - Зависит от `csharp-api`, `postgres`, `redis`
  - При старте делает: `migrate`, `collectstatic`, `runserver`
- `postgres` (PostgreSQL 15)
  - Порт: `5433:5432`
  - Подключен `database/init.sql` (сейчас пустой)
- `redis` (Redis 7)
  - Порт: `6379:6379`
- `nginx`
  - Порт: `80:80`
  - Проксирует на `django-app`, раздает `/static` и `/media`

### Разделение ответственности
- **ASP.NET Core (backend_csharp)**
  - REST API для турниров, MVP-номинантов и голосования.
  - Простая статистика для дашборда.
  - Хранение сущностей `Tournament`, `Nominee`, `Vote` в PostgreSQL через EF Core.
- **Django (frontend_django)**
  - HTML UI (шаблоны) для дашборда, турниров, регистрации, стримов, голосования.
  - Прокси-вызовы к C# API через `core/api_client.py`.
  - Фолбэки на статические данные, если API недоступно.

### Где фронт
- Серверный рендер в Django templates (`core/templates/*.html`) без SPA.

### Где realtime
- **Не найдено**: нет `SignalR Hub`, нет Django Channels consumer, нет websocket endpoint.
- Redis используется только как кэш в Django (`django-redis`), не как pub/sub для live-событий.

---

## B) Сущности и схема данных

## Фактически реализованные сущности
1. `Tournament` (C#)
   - Поля: `Id, Name, Game, PrizePool, MaxParticipants, CurrentParticipants, StartDate, Status`
2. `Nominee` (C#)
   - Поля: `Id, Name, Team, Role, Kda, Rating, Votes`
3. `Vote` (C#)
   - Поля: `Id, NomineeId, VoterSession, VoterIp`

## Не реализованы (по обязательным требованиям)
- `User`, `Role`, `Team`, `Player` (файл `Player.cs` пустой)
- `Discipline`, `Stage/Group`, `Match`, `MatchEvent/LiveState`
- `RatingVerification` (Steam/Faceit)
- `StreamLink`
- `MVPVote` в терминах турнира/этапа (текущая модель голосования есть, но без привязки к финалу турнира)
- `PrizePool` и `Payout/Transaction` как отдельные сущности
- `Stats/Aggregates` по игрокам/дисциплинам

## Критичные поля, которых не хватает
- Статусы этапов/матчей (`planned/live/finished/approved`)
- Таймстампы событий (`created_at`, `updated_at`, `finished_at`, `approved_at`)
- Привязка ролей к операциям (кто может вносить/подтверждать результат)
- Привязка стримов к турниру/матчу и статус онлайн/оффлайн
- Флаги/источник подтверждения рейтинга (`provider`, `verified`, `verified_at`, `raw_payload`)

---

## C) Сопоставление с требованиями (GAP analysis)

| Требование | Где реализовано | Статус | Что добавить для демо |
|---|---|---|---|
| 1) Регистрация команд/игроков + подтверждение рейтинга | Django форма регистрации без БД; C# endpoint отсутствует | **Частично** | Модели Team/Player/Registration + API; мок Steam/Faceit verification endpoint с `DEMO_MODE=true` |
| 2) Сетка single/groups + посев по рейтингу | Есть только отображение `matches` из payload турнира; генератора нет | **Нет** | Endpoint генерации сетки + модели Stage/Group/Match + seeding по verified rating |
| 3) Матчи и результаты + пересчет сетки | Нет моделей/эндпоинтов матчей, нет пересчета | **Нет** | CRUD Match + update result (judge/admin only) + bracket recompute service |
| 4) Realtime трансляция результатов | Не найден websocket/SignalR/Channels | **Нет** | Минимум: SignalR Hub в C# + Redis backplane/pubsub + JS подписка в Django шаблоне |
| 5) MVP голосование | Есть nominees/vote/hasvoted | **Частично** | Привязать к турниру и состоянию финала, отдельное открытие/закрытие голосования |
| 6) Призовой фонд и выплаты | В `Tournament` только число `PrizePool` | **Частично** | Сущности PrizePoolRule/Payout + расчет распределения + статусы выплат |
| 7) Twitch/YouTube интеграция | Страница streams статическая, без привязки к турнирам | **Частично** | API attach stream + mock status endpoint (online/offline/viewers) |
| 8) Аналитика + CSV/PDF | Только агрегат-заглушка в `GetStats()` | **Частично** | Player stats, discipline popularity, endpoint CSV export |
| Роли/доступы | Нет auth/roles; ограничения не применяются | **Нет** | Минимальный RBAC (Player/Captain/Judge/Admin/Spectator) и атрибуты доступа |

---

## D) API/эндпоинты и контракты

## Что есть сейчас
- `GET /api/tournament`
- `GET /api/tournament/{id}`
- `GET /api/tournament/stats`
- `GET /api/voting/nominees`
- `POST /api/voting/vote`
- `GET /api/voting/hasvoted/{sessionId}`
- `GET /api/health`

## Что критично добавить (минимум для защиты)
1. **Auth/Roles**
   - `POST /api/auth/register`
   - `POST /api/auth/login`
   - `GET /api/auth/me`
2. **Турниры и стадии**
   - `POST /api/tournaments`
   - `POST /api/tournaments/{id}/publish|start|stop`
   - `POST /api/tournaments/{id}/stages/generate?format=single|groups`
3. **Команды/игроки/регистрация**
   - `POST /api/teams`, `POST /api/players`
   - `POST /api/tournaments/{id}/registrations`
   - `POST /api/registrations/{id}/approve`
4. **Rating verification (mock)**
   - `POST /api/ratings/verify/steam`
   - `POST /api/ratings/verify/faceit`
   - В `DEMO_MODE=true` отдавать реалистичный JSON: `verified`, `rating`, `profileUrl`, `checkedAt`
5. **Матчи**
   - `GET /api/matches/{id}`
   - `POST /api/matches/{id}/result` (только judge/admin)
   - `POST /api/matches/{id}/status` (`scheduled/live/finished/approved`)
6. **Realtime**
   - `GET /hubs/matches` (SignalR)
   - Группы: `tournament:{id}`, `match:{id}`
7. **MVP**
   - `POST /api/tournaments/{id}/mvp/open`
   - `POST /api/tournaments/{id}/mvp/vote`
   - `GET /api/tournaments/{id}/mvp/results`
8. **Prize pool**
   - `POST /api/tournaments/{id}/prize-pool`
   - `POST /api/tournaments/{id}/prize-pool/distribution`
   - `POST /api/payouts/{id}/status`
9. **Streams**
   - `POST /api/tournaments/{id}/streams`
   - `GET /api/tournaments/{id}/streams/status`
10. **Analytics/report**
   - `GET /api/analytics/dashboard`
   - `GET /api/analytics/player-stats`
   - `GET /api/analytics/discipline-popularity`
   - `GET /api/analytics/export.csv`

## Валидация/DTO/ошибки (текущий риск)
- В C# нет явных DTO-валидаций (`[Required]`, `[Range]`), нет структурированных error-codes.
- В `vote` не проверяется корректность сессии для новых anonymous-посетителей (в Django `session_key` может быть `None`).

---

## E) Реалтайм и Redis

## Текущее состояние
- Redis есть в compose, но только как кэш Django.
- Live event pipeline отсутствует.

## Минимальная учебная реализация
1. Судья вызывает `POST /api/matches/{id}/result`.
2. Backend:
   - валидирует роль (`Judge`/`Admin`),
   - обновляет `Match` и `MatchEvent`,
   - публикует событие `match.updated` в Redis channel `live:match:{id}`,
   - пушит в SignalR группу `match:{id}`.
3. Клиенты:
   - страница матча подписана на SignalR,
   - обновляет счет/статус без перезагрузки.

## Предложение по ключам Redis
- `live:match:{matchId}` — pub/sub канал событий матча
- `live:tournament:{tournamentId}` — события сетки
- `cache:standings:{tournamentId}` — кэш таблицы/сетки
- TTL для кэша: 15–60 сек (для демо)

## Минимальный тест realtime (ручной)
1. Открыть страницу матча в двух браузерах.
2. Под судьей отправить update result.
3. В обеих вкладках увидеть изменение `score/status` без refresh.
4. Проверить, что после `finished` пересчитался следующий матч сетки.

---

## F) Docker и локальный запуск (демо)

## Что уже хорошо
- Есть единый `docker-compose.yml` со всеми ключевыми сервисами.
- Django контейнер автоматически делает `migrate` и `collectstatic`.
- Healthcheck C# API подключен к зависимостям.

## Что мешает стабильной демонстрации
1. `database/init.sql` пустой — нет сидов.
2. C# использует `EnsureCreated()` вместо миграций EF (сложнее контролировать эволюцию схемы).
3. Нет one-command сценария с гарантированными демо-данными (турнир, команды, матчи, MVP, призовые).
4. Нет простой проверки готовности realtime канала.

## Рекомендации строго для учебного демо
- Добавить `scripts/demo-up.sh`:
  - `docker compose up -d --build`
  - ожидание healthcheck
  - запуск сидов (Django management command или C# seed endpoint под `DEMO_MODE`)
- Добавить сид-датасет:
  - 1 турнир, 4–8 команд, рейтинги, generated bracket, 2–3 live/finished матча, финал,
  - открытое MVP-голосование, заполненный prize pool.
- Добавить `scripts/demo-reset.sh` для чистого старта (down -v + up + seed).

## Типовые падения и быстрые фиксы
- DB auth mismatch (`appsettings.json` vs `.env`) → синхронизировать пароль.
- Django не видит C# API → проверить `C_SHARP_API_BASE_URL=http://csharp-api:5000`.
- Статика в nginx пустая → проверить `collectstatic` и volume `static_volume`.

---

## G) Демонстрационный сценарий (5–10 минут)

1. **Логин/регистрация**
   - Войти под `admin` и `judge` (предсозданные пользователи в сиде).
2. **Создание турнира**
   - Создать турнир, выбрать дисциплину и формат (`single`/`groups`).
3. **Регистрация команды/игроков + рейтинг**
   - Подать 4 команды, у 1–2 игроков показать mock-verify Steam/Faceit.
4. **Генерация сетки + посев**
   - Нажать “Generate Bracket”, показать порядок по подтвержденному рейтингу.
5. **Открытие матча**
   - Перевести матч в статус `live`.
6. **Live-обновление счета**
   - Судья меняет счет; зрительская вкладка обновляется без refresh.
7. **Завершение и пересчет**
   - Завершить матч/финал, показать продвижение победителя по сетке.
8. **MVP голосование**
   - Открыть голосование, проголосовать из 2 разных сессий, показать блок повторного голоса.
9. **Призовой фонд**
   - Создать фонд, задать распределение 50/30/20, показать статусы выплат.
10. **Аналитика + CSV**
   - Показать player stats, популярность дисциплин, скачать CSV.

---

## H) Top-10 задач (приоритет)

1. **[S] Ввести RBAC и auth (минимум JWT/cookie) + роли `player/captain/judge/admin/spectator`**  
   Зависимость: базовая для ограничения ввода результатов.
2. **[S] Добавить модели `Team`, `Player`, `TournamentRegistration` и API регистрации**  
   Зависимость: нужно для сетки и аналитики.
3. **[S] Добавить `RatingVerification` (Steam/Faceit mock) с `DEMO_MODE`**  
   Зависимость: без verified rating нет корректного seeding.
4. **[M] Реализовать генератор сетки (`single`, `groups`) и модели `Stage/Group/Match`**  
   Зависимость: после команд/рейтингов.
5. **[M] Реализовать update result + подтверждение судьей + recompute bracket**  
   Зависимость: после match-моделей и ролей.
6. **[M] Добавить realtime (SignalR + Redis pub/sub) для матчей**  
   Зависимость: после match events и update-result API.
7. **[S] Доработать MVP как сущность турнира (`open/close/results`)**  
   Зависимость: после турниров и финального статуса.
8. **[M] Добавить `PrizePool` + `Payout` и расчет распределения**  
   Зависимость: после финальных мест турнира.
9. **[S] Интеграция стримов (Twitch/YouTube mock) + привязка к матчу/турниру**  
   Зависимость: независимая.
10. **[S] Аналитика + CSV export + демо-сиды + demo-up/demo-reset scripts**  
   Зависимость: финализирует “показать работающим”.

---

## Итог
Сейчас проект — хороший каркас для UI + базового API, но до полного обязательного функционала по учебному ТЗ не хватает ядра доменной модели турнира (команды/матчи/стадии/роли/realtime/призовые/аналитика). Для защиты рекомендуется сфокусироваться на **минимально рабочем end-to-end потоке** из пункта G с моками интеграций и предзаполненными данными.
