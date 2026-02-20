# Система для организации киберспортивных турниров

Единая точка входа в проект — **nginx на порту 80**.

> ВАЖНО: открывайте `http://localhost/`, а не `http://localhost:8000/`.
> Контейнеры `django-app` и `csharp-api` работают только во внутренней docker-сети и наружу не публикуются.

## Быстрый старт (Windows PowerShell)

```powershell
docker compose down --remove-orphans
docker compose up -d --build
docker compose ps
docker compose logs -f nginx
```

## Smoke-check (Windows PowerShell)

```powershell
curl -i http://localhost/
curl -i http://localhost/api/health
curl -i -X POST "http://localhost/hubs/matches/negotiate?negotiateVersion=1"
curl http://localhost/ | Select-Object -First 20
```

## Действующие ссылки

### UI (Django через nginx)

| Ссылка | Что это |
|---|---|
| `http://localhost/` | Главная (dashboard) |
| `http://localhost/login/` | Вход |
| `http://localhost/logout/` | Выход |
| `http://localhost/tournaments/` | Список турниров |
| `http://localhost/tournaments/1/` | Карточка турнира (пример) |
| `http://localhost/tournaments/1/matches/` | Матчи турнира (пример) |
| `http://localhost/tournaments/1/mvp/` | MVP турнира (пример) |
| `http://localhost/analytics/` | Аналитика |
| `http://localhost/registration/` | Регистрация |
| `http://localhost/streams/` | Стримы |
| `http://localhost/voting/` | Голосование |
| `http://localhost/admin/` | Django Admin |

### API (C# через nginx)

| Ссылка | Что это |
|---|---|
| `http://localhost/api/health` | Health-check API |
| `http://localhost/api/auth/login` | Логин |
| `http://localhost/api/tournament` | Турниры |
| `http://localhost/api/demo/seed` | Demo seed endpoint (алиас к mock ratings) |
| `http://localhost/api/ratings/mock` | Mock ratings |

### SignalR (C# через nginx)

| Ссылка | Что это |
|---|---|
| `http://localhost/hubs/matches` | Hub endpoint |
| `http://localhost/hubs/matches/negotiate?negotiateVersion=1` | Negotiate endpoint |

## Если порт 80 занят

Измените публикацию порта nginx в `docker-compose.yml`, например на `8080:80`, затем перезапустите:

```powershell
docker compose down --remove-orphans
docker compose up -d --build
```

После этого используйте ссылки вида `http://localhost:8080/...`.
