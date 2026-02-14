# Киберспортивный турнир (Django + C# API)

## API↔UI map

| UI action/page | Django method | HTTP method + path | Payload / response fields | Roles |
|---|---|---|---|---|
| Login page | `api_client.login` | `POST /api/auth/login` | req: `email,password`; res: `token,user{email,role}` | public |
| Load current user | `api_client.me` | `GET /api/auth/me` | res: `email,role` | authenticated |
| Tournament list | `api_client.get_tournaments` | `GET /api/tournament` | res item: `id,name,discipline,format,status,startDate,prizePool,totalAmount` | public |
| Tournament detail | `api_client.get_tournament` | `GET /api/tournament/{id}` | same as above + `stagesSummary` | public |
| Match page | `api_client.get_matches` | `GET /api/matches?tournamentId={id}` | res item: `id,tournamentId,teamA,teamB,scoreA,scoreB,status,round,groupName,streamUrl` | public |
| Update match result | `api_client.update_match_result` | `PUT /api/matches/{id}/result` | req: `scoreA,scoreB` | admin/judge |
| MVP page | `api_client.get_mvp` | `GET /api/mvp/results?tournamentId={id}` | res: `isOpen,candidates[],results[]` | public |
| MVP vote | `api_client.vote_mvp` | `POST /api/mvp/vote` | req: `tournamentId,playerId` | captain/admin/judge |
| Streams page | `api_client.get_streams` | `GET /api/streams/status` | res item: `provider,url,status{online,viewers}` | public |
| Analytics page | `api_client.get_analytics` | `GET /api/analytics` | res: `playerStats[],disciplinePopularity[]` | public |
| Registration approve | N/A (UI button RBAC-only) | `POST /api/registrations/{id}/approve` | res: `registrationId,status` | admin |
| Stage generation | N/A (UI button RBAC-only) | `POST /api/stages/generate/single` / `groups` | res: `stageType,generated` | admin |
| Seed/rating demo | N/A | `GET /api/ratings/mock` | res: `playerId,rating` | admin |

## Smokes

```bash
curl -s http://localhost/api/health
curl -s -X POST http://localhost/api/auth/register -H 'Content-Type: application/json' -d '{"email":"captain@example.com","password":"secret123","role":"captain"}'
curl -s -X POST http://localhost/api/auth/login -H 'Content-Type: application/json' -d '{"email":"admin@example.com","password":"secret123"}'
curl -s -H "Authorization: Bearer <TOKEN>" http://localhost/api/auth/me
curl -s -X POST http://localhost/api/registrations/15/approve
curl -s -X POST http://localhost/hubs/matches/negotiate
```

Demo flow в браузере:
1. Открыть `/login`, войти как `admin@example.com`.
2. Открыть `/tournaments`, затем страницу турнира, матчи и MVP.
3. Проверить, что RBAC-кнопки видны только нужным ролям.
4. Открыть `/streams` и `/analytics`.

## Notes

- Server-side вызовы Django идут через `DJANGO_API_BASE_URL` (по умолчанию `http://csharp-api:5000/api`).
- Browser/API gateway путь: `PUBLIC_API_BASE_URL` (по умолчанию `/api`).
- Все ошибки в Django нормализуются в формат: `{ok,data,error{code,message,details}}`.
