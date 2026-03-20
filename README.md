# Система для организации киберспортивных турниров

Учебный full-stack проект: ASP.NET Core API + Django templates UI + nginx + Postgres + Redis + Docker Compose.

## Публичные ссылки
- UI: http://localhost/
- API: http://localhost/api/
- Health: http://localhost/api/health
- SignalR hub: http://localhost/hubs/matches

## Внутренние адреса контейнеров
- csharp-api:5000 — внутренний backend URL
- django-app:8000 — внутренний Django UI URL
- postgres:5432 — PostgreSQL
- redis:6379 — Redis

## Переменные окружения
Основные значения задаются в `.env`.

- `PANDASCORE_TOKEN` — токен PandaScore для внешних турниров/игроков/стримов
- `TWITCH_CLIENT_ID` — optional
- `TWITCH_ACCESS_TOKEN` — optional
- `YOUTUBE_API_KEY` — optional

## Запуск (Windows PowerShell)
```powershell
docker compose up -d --build
```

Если нужно полностью сбросить БД/тома:
```powershell
docker compose down -v --remove-orphans
docker compose up -d --build
```

## Demo
Windows:
```powershell
./scripts/demo-up.ps1
./scripts/demo-reset.ps1
./scripts/smoke.ps1
```

Linux/macOS:
```bash
./scripts/demo-up.sh
./scripts/demo-reset.sh
./scripts/smoke.sh
```

## Сценарий защиты 5–10 минут
1. Открыть `http://localhost/`.
2. Зарегистрировать пользователя и войти.
3. Открыть профиль, изменить ник/описание, подтвердить рейтинг через mock Faceit/Steam.
4. Создать команду, добавить игроков, при необходимости подтвердить рейтинг игрока судьёй/админом.
5. Под админом открыть `/tournaments`, создать локальный турнир.
6. Под капитаном подать заявку на локальный турнир.
7. Под судьёй/админом на странице турнира принять заявку, сохранить параметры сетки.
8. Открыть вкладки турнира: Обзор, Матчи, Стримы, MVP, Аналитика, Призовые.
9. Проверить `/api/health` и экспорт аналитики CSV через API.

## Troubleshooting
- Если nginx уходит в restarting, проверь `nginx/nginx.conf`.
- Если БД в странном состоянии, используй `docker compose down -v --remove-orphans`.
- Если PandaScore не настроен, внешние турниры/стримы могут быть недоступны, но локальный сценарий защиты должен работать.
