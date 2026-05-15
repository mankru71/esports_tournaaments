# Arena Control

Веб-платформа для организации киберспортивных турниров: команды, заявки, сетка, live-матчи, Discord-уведомления, MVP-голосование, призовые, стримы и аналитика.

## Запуск

```bash
docker compose down -v --remove-orphans
docker compose up -d --build
```

Перед запуском при необходимости заполните `.env`: `DISCORD_WEBHOOK_URL`, `PANDASCORE_TOKEN`, `FACEIT_API_KEY`, SMTP-настройки.

После запуска:

- интерфейс: http://localhost/
- API: http://localhost/api/
- health-check: http://localhost/api/health
- Discord status: http://localhost/api/discord/status

## Основные сценарии

1. Создать турнир.
2. Создать команду и добавить игроков.
3. Подтвердить рейтинг игроков.
4. Подать и принять заявку на турнир.
5. Сгенерировать сетку или групповой этап.
6. Обновлять результаты матчей в матч-центре.
7. Проверить уведомление Discord при live-матче.
8. Привязать Twitch/YouTube-стрим к матчу.
9. Провести голосование за MVP.
10. Настроить и рассчитать призовые.
11. Открыть аналитику и CSV-экспорт.
