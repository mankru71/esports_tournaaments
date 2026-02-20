# Система для организации киберспортивных турниров

Единая точка входа для браузера — **nginx на порту 80**: `http://localhost/`.

## Env vars

Создайте `.env` в корне проекта:

```env
DJANGO_API_BASE_URL=http://csharp-api:5000/api
PUBLIC_API_BASE_URL=http://localhost/api
```

Дополнительно (опционально):

```env
DB_NAME=esports_db
DB_USER=esports_user
DB_PASSWORD=esports123
DB_HOST=postgres
DB_PORT=5432
```

`docker-compose.yml` прокидывает эти переменные в `django-app` через `env_file` и `environment`.

## Public vs Internal URLs

### Публичные ссылки (открывать в браузере)
- `http://localhost/`
- `http://localhost/api/...`
- `http://localhost/hubs/...`

### Внутренние ссылки (только внутри docker-сети)
- `http://csharp-api:5000/api`

**Важно: `csharp-api:5000` не откроется в браузере с хоста, если порт 5000 не опубликован наружу. Это нормально.**

## Быстрый запуск (Windows PowerShell)

```powershell
docker compose down --remove-orphans
docker compose up -d --build
docker compose ps
docker compose exec django-app env | findstr DJANGO_API_BASE_URL
curl -i http://localhost/api/health
curl -i http://localhost/
curl -i http://localhost/api/auth/me
```

## Smoke-check

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\smoke.ps1
```

В `scripts/smoke.ps1` проверяются:
- `GET /api/health`
- `POST /api/auth/register`
- `POST /api/auth/login`
- `GET /api/auth/me` (с токеном после login)
- `GET /api/tournament`
- `GET /api/teams`

## Registration troubleshooting

Если регистрация не проходит:
1. Убедитесь, что API доступно через `http://localhost/api/health`.
2. Проверьте, что пароль минимум 8 символов.
3. Проверьте уникальность email (иначе API вернёт `409 Conflict`).
4. Проверьте переменную `DJANGO_API_BASE_URL` внутри `django-app`.

## Theme toggle troubleshooting

Если тема не переключается:
1. Очистите кэш браузера (Ctrl+F5).
2. Проверьте, что загружается `/static/js/app.js`.
3. Проверьте `localStorage['theme']` в DevTools.
4. Убедитесь, что на `<html>` меняется `data-theme` (`dark`/`light`).
