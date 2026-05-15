# Esports Tournaments

Учебная система для организации киберспортивных турниров: локальные турниры, команды, заявки, сетка матчей, обновление счёта, MVP-голосование, аналитика и демонстрационная интеграция с Discord.

## Стек

- **Backend:** ASP.NET Core 8, Entity Framework Core, PostgreSQL, SignalR.
- **Frontend:** Django templates + Bootstrap-like UI.
- **Infra:** Docker Compose, nginx, Redis.
- **External integrations:** Discord Webhooks, PandaScore/Faceit как optional-интеграции.

## Быстрый запуск

1. Скопируй настройки:

```bash
cp .env.example .env
```

2. Для Discord-демо вставь webhook URL в `.env`:

```env
DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/...
DISCORD_BOT_NAME=Esports Arena
PUBLIC_FRONTEND_URL=http://localhost
```

3. Запусти проект:

```bash
docker compose up -d --build
```

4. Открой:

- UI: <http://localhost/>
- API health: <http://localhost/api/health>
- Discord status: <http://localhost/api/discord/status>
- SignalR hub: <http://localhost/hubs/matches>

Полный сброс базы и контейнеров:

```bash
docker compose down -v --remove-orphans
docker compose up -d --build
```

## Как показать Discord-интеграцию на защите

Discord Webhook работает без отдельного бота: backend делает HTTP POST на URL вебхука, а сообщение появляется в выбранном канале сервера.

1. В Discord создай сервер или выбери существующий канал.
2. Открой **Channel Settings → Integrations → Webhooks → New Webhook → Copy Webhook URL**.
3. Вставь URL в `.env` в переменную `DISCORD_WEBHOOK_URL`.
4. Перезапусти контейнеры:

```bash
docker compose up -d --build
```

5. На сайте зайди под админом и создай новый турнир. В Discord должен появиться embed с названием турнира, дисциплиной, датой и ссылкой.
6. Сгенерируй сетку и открой страницу матчей. Поставь счёт, например `1:0`. Матч перейдёт в статус **LIVE**, а Discord получит уведомление о live-матче.
7. Если поставить счёт `16:10`, матч завершится и победитель пройдёт дальше по сетке.

Дополнительная проверка webhook-а:

```bash
curl http://localhost/api/discord/status
```

Тестовый POST `/api/discord/test` защищён admin-токеном.

## Сценарий защиты на 5–10 минут

1. Открыть `http://localhost/` и показать список турниров.
2. Зарегистрироваться/войти.
3. Под капитаном создать команду и добавить игроков.
4. Под админом создать локальный турнир — сразу показать сообщение в Discord.
5. Под капитаном подать заявку на турнир.
6. Под админом или судьёй принять заявку.
7. Сгенерировать сетку турнира.
8. Открыть матч-центр, обновить счёт `1:0` и показать Discord-уведомление о LIVE.
9. Показать MVP-голосование, призовые и аналитику.
10. Открыть `http://localhost/api/health` как доказательство, что backend живой.

## Полезные команды

Windows PowerShell:

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

## Переменные окружения

Основное лежит в `.env.example`.

| Переменная | Назначение |
|---|---|
| `DISCORD_WEBHOOK_URL` | URL Discord webhook-а для уведомлений |
| `DISCORD_BOT_NAME` | Имя отправителя сообщений в Discord |
| `PUBLIC_FRONTEND_URL` | Публичный URL сайта для ссылок в email/Discord |
| `PANDASCORE_TOKEN` | Optional: внешний API для турниров/игроков |
| `FACEIT_API_KEY` | Optional: Faceit API для проверки профиля |
| `SMTP_*` | Optional: SMTP; если пусто, ссылка подтверждения email выводится в логи backend-а |

## Что было поправлено

- Исправлен сломанный Django `views.py`: страница турнира, роли, профиль и подтверждение email.
- Исправлен endpoint подтверждения email между Django и C# API.
- Исправлена регистрация C# сервисов в DI-контейнере.
- Исправлены Docker/env-настройки для C# API, Django, внешних API и Discord.
- Добавлен nginx proxy для SignalR `/hubs/`.
- Добавлены Discord-уведомления при создании турнира и переводе матча в LIVE.
- Удалены/перенесены пустые и неправильно расположенные файлы.
- Убраны реальные токены из конфигов; оставлены безопасные placeholders.
- Добавлены защиты от дублей при approve заявки и от FK-ошибок при удалении турниров/команд.

## Troubleshooting

- Если контейнеры не стартуют, сначала проверь `.env` и затем выполни `docker compose logs -f`.
- Если база в странном состоянии после старой версии проекта: `docker compose down -v --remove-orphans`.
- Если Discord молчит, проверь `DISCORD_WEBHOOK_URL` и `http://localhost/api/discord/status`.
- Если PandaScore/Faceit не настроены, локальный сценарий всё равно работает — эти API не обязательны для защиты.


## Что добавлено по ТЗ после аудита

Проект теперь закрывает основные пункты ТЗ:

- регистрация команд и игроков с ручным/внешним рейтингом;
- подтверждение рейтинга администратором или судьёй;
- запрет принятия заявки, если у команды нет игроков или не подтверждён рейтинг;
- одиночная сетка и групповой этап с посевом по среднему рейтингу;
- таблица групп с очками после завершения матчей;
- live-обновление результатов через SignalR;
- Discord-уведомления при создании турнира и переходе матча в LIVE;
- MVP-голосование с реальными кандидатами из команд турнира;
- управление статусами этапа: planned/live/paused/finished;
- привязка Twitch/YouTube ссылки к конкретному матчу;
- распределение призового фонда и подтверждение выплат;
- расширенная аналитика: игроки, рейтинги, winrate, дисциплины, live-матчи, стримы;
- CSV-экспорт аналитики.

Подробный чек-лист лежит в `docs/tz_coverage.md`.

## Обновлённый сценарий защиты

1. Войти под администратором.
2. Создать турнир — показать Discord-уведомление.
3. Создать команду/игроков, указать рейтинги.
4. Подтвердить рейтинг игроков на странице «Команды».
5. Подать заявку команды на турнир и принять её.
6. Выбрать формат single или groups и сгенерировать сетку.
7. В матч-центре поставить счёт `1:0` — матч станет LIVE, обновится через SignalR и уйдёт Discord-уведомление.
8. Привязать Twitch/YouTube стрим к матчу на вкладке «Стримы».
9. Завершить финал счётом `16:10` — откроется MVP.
10. Открыть MVP-голосование, проголосовать и показать результаты.
11. Распределить призовой фонд и подтвердить выплаты.
12. Открыть аналитику и скачать CSV.
