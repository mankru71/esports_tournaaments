# Система для организации киберспортивных турниров

Единая точка входа — **nginx на порту 80** (`http://localhost/`).

## Почему раньше было «API недоступно»

Корневая проблема: в `docker-compose.yml` для Django был задан `C_SHARP_API_BASE_URL=http://csharp-api:5000` без суффикса `/api`, а `api_client.py` вызывал `auth/login`, `tournament` и т.д. как относительные пути. В итоге запросы уходили в несуществующие URL вроде `http://csharp-api:5000/auth/login` (404), UI показывал «Ошибка API» и включал фолбэки.

Исправлено:
- server-side base URL Django: `DJANGO_API_BASE_URL=http://csharp-api:5000/api`;
- public URL (для браузера/документации): `PUBLIC_API_BASE_URL=http://localhost/api`;
- nginx оставлен как единая точка входа: `/ -> django-app:8000`, `/api/ -> csharp-api:5000`, `/hubs/ -> csharp-api:5000`.

## Быстрый запуск (PowerShell)

```powershell
docker compose down --remove-orphans
docker compose up -d --build
docker compose ps
```

## Smoke-check

### PowerShell

```powershell
./scripts/smoke.ps1
```

### Bash

```bash
./scripts/smoke.sh
```

Проверки внутри smoke:
- `GET /api/health`
- `POST /api/auth/login`
- `GET /api/tournament`

## Основные ссылки

- `http://localhost/` — Главная
- `http://localhost/login/` — Вход
- `http://localhost/registration/` — Регистрация
- `http://localhost/tournaments/` — Турниры
- `http://localhost/teams/` — Команды
- `http://localhost/api/health` — Health API

## Что теперь работает end-to-end

- Логин/регистрация через C# API с сохранением пользователя в БД.
- Django сохраняет токен в session и отправляет Bearer в API.
- Турниры/команды загружаются из реального API.
- Демо-фолбэки показываются только при реальной недоступности API (connection error).
- Переключение темы dark/light корректно применяется и сохраняется в `localStorage`.
