# Discord Webhook demo

## Что реализовано

Backend отправляет Discord-уведомления в двух местах:

1. `TournamentController.Create` — после создания нового локального турнира.
2. `MatchesController.SetMatchResult` — когда матч впервые переходит в статус `live`.

Код интеграции лежит в `backend_csharp/Services/DiscordWebhookService.cs`.
Диагностика доступна в `backend_csharp/Controllers/DiscordController.cs`.

## Настройка

В `.env`:

```env
DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/...
DISCORD_BOT_NAME=Esports Arena
PUBLIC_FRONTEND_URL=http://localhost
```

После изменения `.env` перезапустить контейнеры:

```bash
docker compose up -d --build
```

## Проверка

```bash
curl http://localhost/api/discord/status
```

Ожидаемо:

```json
{"enabled":true,"message":"Discord webhook configured"}
```

## Демонстрационный сценарий

1. Зайти на сайт под админом.
2. Создать турнир на странице `/tournaments/`.
3. Показать, что в Discord-канале появился embed о турнире.
4. Принять заявку команды и сгенерировать сетку.
5. Открыть `/tournaments/{id}/matches/`.
6. Поставить счёт `1:0`.
7. Показать, что матч стал `LIVE`, а Discord получил уведомление.
