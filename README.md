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
| `http://localhost/` | Главная |
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

## UI заметки

- Реализованы две темы интерфейса: тёмная (по умолчанию) и светлая.
- Переключатель темы находится в правой части верхней панели: кнопка **🌙/☀️ Тёмная/Светлая**.
- Выбор темы сохраняется в `localStorage` (ключ `theme`) и восстанавливается при следующем открытии сайта.
- Все страницы используют единый layout и стили (`Bootstrap 5 + static/css/app.css`).

## UI demo checklist (визуальная проверка)

1. Открыть `http://localhost/` и проверить читабельность шапки.
2. Нажать переключатель темы и убедиться, что цвета меняются без перезагрузки.
3. Перезагрузить страницу и убедиться, что выбранная тема сохранилась.
4. Проверить страницу входа: карточка, поля, кнопки и подсказки.
5. Проверить страницу регистрации: ошибки формы и единый стиль с входом.
6. Проверить таблицу турниров на мобильной ширине (горизонтальный скролл у таблицы).
7. Открыть карточку турнира и убедиться в отображении статусов бейджей.
8. Открыть страницы матчей, MVP, стримов, аналитики и голосования.
9. Проверить empty-state блоки на страницах без данных.
10. Проверить, что все тексты интерфейса отображаются на русском языке.

## Если порт 80 занят

Измените публикацию порта nginx в `docker-compose.yml`, например на `8080:80`, затем перезапустите:

```powershell
docker compose down --remove-orphans
docker compose up -d --build
```

После этого используйте ссылки вида `http://localhost:8080/...`.
