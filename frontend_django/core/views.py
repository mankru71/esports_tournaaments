from datetime import date
from django.contrib import messages
from django.http import HttpResponse, HttpResponseForbidden
from django.shortcuts import redirect, render
from django.urls import reverse
from .api_client import api_client
from .forms import (
    FaceitVerifyForm,
    LoginForm,
    MatchResultForm,
    ProfileEditForm,
    RegistrationForm,
    ScoutingEditForm,
    TeamCreateForm,
    TeamPlayerForm,
    TeamVacancyForm,
    TournamentCreateForm,
)
from functools import wraps

def login_required(view_func):
    @wraps(view_func)
    def _wrapped_view(request, *args, **kwargs):
        token = request.session.get("api_token") or request.COOKIES.get("api_token")
        if not token:
            messages.info(request, "Войдите в аккаунт")
            return redirect("login")
        return view_func(request, *args, **kwargs)
    return _wrapped_view

STATUS_LABELS = {
    "planned": "Запланирован",
    "live": "Идёт",
    "paused": "Пауза",
    "finished": "Завершён",
    "approved": "Подтверждён",
    "pending": "На рассмотрении",
    "rejected": "Отклонена",
}
def _group_bracket_rounds(matches: list[dict]) -> list[dict]:
    buckets = {}
    for m in matches:
        label = m.get("round") or "Round"
        if label not in buckets:
            buckets[label] = {
                "label": label,
                "roundNumber": m.get("roundNumber", 0),
                "matches": [],
            }
        buckets[label]["matches"].append(m)

    return sorted(buckets.values(), key=lambda r: r["roundNumber"])

def _normalize_status(value):
    key = (value or "planned").lower()
    return key, STATUS_LABELS.get(key, value or "Запланирован")


def _pick_display_name(item: dict, fallback: str = "—") -> str:
    if not isinstance(item, dict):
        return fallback
    for key in ("displayName", "name", "nickname", "playerName", "player", "title"):
        value = item.get(key)
        if value not in (None, ""):
            return str(value)
    return fallback


def _normalize_mvp_payload(payload):
    payload = payload or {}

    def normalize_player(item):
        item = dict(item or {})
        item["displayName"] = _pick_display_name(item, "Игрок")
        item["teamName"] = item.get("team") or item.get("teamName") or ""
        item["votes"] = item.get("votes") or item.get("voteCount") or 0
        return item

    payload["candidates"] = [normalize_player(x) for x in payload.get("candidates", []) or []]
    payload["results"] = [normalize_player(x) for x in payload.get("results", []) or []]
    payload["isOpen"] = bool(payload.get("isOpen", False))
    return payload


def _normalize_tournament(item):
    item = item or {}
    status_key, status_label = _normalize_status(item.get("status", "planned"))
    return {
        "id": item.get("id"),
        "name": item.get("name", "н/д"),
        "discipline": item.get("discipline") or item.get("game") or "н/д",
        "format": item.get("format", "н/д"),
        "stageType": item.get("stageType") or item.get("stage_type") or "single",
        "status": status_key,
        "status_label": status_label,
        "startDate": item.get("startDate") or item.get("start_date") or "н/д",
        "prizePool": item.get("prizePool") or item.get("prize_pool") or item.get("totalAmount") or "н/д",
        "participants": item.get("participants") or f"{item.get('currentParticipants', '0')}/{item.get('maxParticipants', '0')}",
        "prizePayouts": item.get("prizePayouts") or [],
        "isExternal": bool(item.get("isExternal")),
        "provider": item.get("provider"),
        "stagesSummary": item.get("stagesSummary") or "",
        "maxParticipants": item.get("maxParticipants", 0),
        "currentParticipants": item.get("currentParticipants", 0),
    }


def _normalize_match(item):
    item = item or {}
    status_key, status_label = _normalize_status(item.get("status", "planned"))
    return {
        "id": item.get("id"),
        "team_a_id": item.get("team_a_id"),
        "team_b_id": item.get("team_b_id"),
        "teamA": item.get("teamA", "н/д"),
        "teamB": item.get("teamB", "н/д"),
        "scoreA": item.get("scoreA", 0),
        "scoreB": item.get("scoreB", 0),
        "status": status_key,
        "status_label": status_label,
        "round": item.get("round", "н/д"),
        "groupName": item.get("groupName", ""),
        "streamUrl": item.get("streamUrl", "") or "",
        "prediction": _normalize_prediction(item.get("prediction")),
        "userPredictionTeamId": item.get("userPredictionTeamId"),
    }


def _normalize_prediction(raw):
    """Прогноз нейросети (Dota 2): {probA, probB} в процентах или None."""
    if not isinstance(raw, dict):
        return None
    prob_a = raw.get("teamAWinProbability")
    prob_b = raw.get("teamBWinProbability")
    if prob_a is None or prob_b is None:
        return None
    try:
        prob_a = round(float(prob_a), 1)
        prob_b = round(float(prob_b), 1)
    except (TypeError, ValueError):
        return None
    return {
        "probA": prob_a,
        "probB": prob_b,
        "favorite": "A" if prob_a >= prob_b else "B",
    }


def _detect_stream_provider(url: str) -> str:
    u = (url or "").lower()
    if "twitch.tv" in u:
        return "Twitch"
    if "youtube.com" in u or "youtu.be" in u:
        return "YouTube"
    return "Stream"


def _extract_twitch_channel(url: str) -> str:
    try:
        from urllib.parse import urlparse
        parsed = urlparse(url)
        parts = [part for part in parsed.path.split("/") if part]
        return parts[0] if parts else ""
    except Exception:
        return ""


def _build_youtube_embed(url: str) -> str:
    try:
        from urllib.parse import parse_qs, urlparse
        parsed = urlparse(url)
        if parsed.netloc.endswith("youtu.be"):
            video_id = parsed.path.strip("/")
        elif parsed.path.startswith("/live/"):
            video_id = parsed.path.split("/live/")[1].split("?")[0].strip("/")
        else:
            video_id = parse_qs(parsed.query).get("v", [""])[0]
        return f"https://www.youtube.com/embed/{video_id}" if video_id else url
    except Exception:
        return url


def _read_current_user(request):
    token = request.session.get("api_token")
    if not token or api_client.token_expired(token):
        request.session.pop("api_token", None)
        request.session.pop("current_user", None)
        return None

    cached_user = request.session.get("current_user")
    if cached_user and cached_user.get("isEmailVerified"):
        return cached_user

    me_result = api_client.me(token)
    if not me_result.ok:
        request.session.pop("api_token", None)
        request.session.pop("current_user", None)
        return None

    request.session["current_user"] = me_result.data
    return me_result.data


def _role_flags(user):
    is_verified = bool((user or {}).get("isEmailVerified", False))
    
    role = ((user or {}).get("role") or "").lower() if is_verified else ""
    
    is_admin = role == "admin"
    is_judge = role == "judge"
    is_captain = role == "captain"
    return {
        "is_admin": is_admin,
        "is_judge": is_judge,
        "is_captain": is_captain,
        "is_player": role == "player",
        "is_viewer": role in ("", "viewer") or not is_verified,
        "is_organizer": is_admin,
        "current_user": user,
    }


def _add_api_error(request, result, fallback="Ошибка выполнения операции"):
    messages.error(request, (result.error or {}).get("message", fallback))


def _load_favorite_ids(token) -> set[int]:
    """ID турниров в избранном текущего пользователя (пустое множество для гостей)."""
    if not token:
        return set()
    result = api_client.get_favorites(token)
    if not result.ok:
        return set()
    ids = (result.data or {}).get("tournamentIds") or []
    favorite_ids = set()
    for raw in ids:
        try:
            favorite_ids.add(int(raw))
        except (TypeError, ValueError):
            continue
    return favorite_ids


def _handle_toggle_favorite(request, token):
    """Общий обработчик action=toggle_favorite: добавляет/убирает турнир из избранного."""
    tournament_id = int(request.POST.get("tournament_id", "0") or 0)
    currently_favorited = (request.POST.get("favorited") or "0") == "1"
    result = (
        api_client.remove_favorite(tournament_id, token=token)
        if currently_favorited
        else api_client.add_favorite(tournament_id, token=token)
    )
    if result.ok:
        messages.success(request, "Удалено из избранного" if currently_favorited else "Добавлено в избранное")
    else:
        _add_api_error(request, result, "Не удалось обновить избранное")


def _require_auth(request, token):
    if not token:
        messages.info(request, "Войдите в аккаунт")
        return redirect("login")
        
    user = request.session.get("current_user") or {}
    
    if user and not user.get("isEmailVerified") and getattr(request.resolver_match, "url_name", "") != "profile":
        messages.warning(request, "Для этого действия необходимо подтвердить почту в профиле.")
        return redirect("profile")
        
    return None


def verify_email_view(request):
    user_id = request.GET.get("userId")
    verify_token = request.GET.get("token")

    if not verify_token:
        messages.error(request, "Ссылка подтверждения некорректна")
        return redirect("login")

    # Новый формат ссылки — только токен (пользователь ищется по нему);
    # старый формат userId+token поддерживаем для уже отправленных писем
    result = (
        api_client.confirm_email(user_id, verify_token)
        if user_id
        else api_client.confirm_email_by_token(verify_token)
    )
    if result.ok:
        messages.success(request, "Почта подтверждена! Войдите в аккаунт.")
        # Удаляем кэш профиля, чтобы Django обновил статус верификации
        request.session.pop("current_user", None)
    else:
        _add_api_error(request, result, "Ссылка недействительна или устарела")

    return redirect("profile" if request.session.get("api_token") else "login")


ACTIVITY_META = {
    "tournament_created": ("Турнир", "status-finished"),
    "team_created": ("Команда", "status-live"),
    "team_deleted": ("Команда", "status-rejected"),
    "player_joined": ("Трансфер", "status-live"),
    "player_left": ("Трансфер", "status-paused"),
    "application_approved": ("Заявка", "status-approved"),
    "match_finished": ("Матч", "status-finished"),
    "external_sync": ("Liquipedia", "status-planned"),
}


def _humanize_time(value: str | None) -> str:
    if not value:
        return ""
    try:
        from datetime import datetime, timezone
        raw = value.replace("Z", "+00:00")
        moment = datetime.fromisoformat(raw)
        if moment.tzinfo is None:
            moment = moment.replace(tzinfo=timezone.utc)
        delta = datetime.now(timezone.utc) - moment
        seconds = max(0, int(delta.total_seconds()))
        if seconds < 60:
            return "только что"
        if seconds < 3600:
            return f"{seconds // 60} мин назад"
        if seconds < 86400:
            return f"{seconds // 3600} ч назад"
        return f"{seconds // 86400} дн назад"
    except (ValueError, TypeError):
        return ""


def dashboard(request):
    stats_result = api_client.get_stats()
    payload = stats_result.data or {}
    stats = {
        "players": payload.get("totalPlayers", 0),
        "tournaments": payload.get("activeTournaments", 0),
        "viewers": payload.get("totalViewers", 0),
        "events_today": payload.get("eventsToday", 0),
        "today": date.today(),
        "most_popular": payload.get("mostPopularDiscipline", "н/д"),
    }
    if not stats_result.ok:
        messages.info(request, (stats_result.error or {}).get("message", "Сводка временно недоступна"))

    # ── Live-матчи (+ Twitch-эмбед первого матча со стримом) ────────────
    live_result = api_client.get_live_matches()
    live_matches = []
    twitch_embed = None
    if live_result.ok:
        for item in live_result.data or []:
            item = dict(item or {})
            item["streamUrl"] = item.get("streamUrl") or ""
            item["prediction"] = _normalize_prediction(item.get("prediction"))
            live_matches.append(item)
        for m in live_matches:
            if "twitch.tv" in m["streamUrl"].lower():
                channel = _extract_twitch_channel(m["streamUrl"])
                if channel:
                    twitch_embed = {"channel": channel, "match": m}
                    break

    # ── Открытые регистрации: запланированные ЛОКАЛЬНЫЕ турниры ────────
    tournaments_result = api_client.get_tournaments()
    open_tournaments = []
    if tournaments_result.ok:
        open_tournaments = [
            _normalize_tournament(item)
            for item in (tournaments_result.data or [])
            if (item or {}).get("status") == "planned" and not (item or {}).get("isExternal")
        ][:6]

    # ── Зал славы ───────────────────────────────────────────────────────
    hof_result = api_client.get_hall_of_fame()
    hall_of_fame = (hof_result.data or []) if hof_result.ok else []

    # ── Лента событий ───────────────────────────────────────────────────
    activity_result = api_client.get_activity(limit=10)
    activity = []
    if activity_result.ok:
        for item in activity_result.data or []:
            item = dict(item or {})
            label, css = ACTIVITY_META.get(item.get("actionType", ""), ("Событие", "status-planned"))
            item["type_label"] = label
            item["type_css"] = css
            item["time_label"] = _humanize_time(item.get("timestampUtc"))
            activity.append(item)

    twitch_parent = (request.get_host() or "localhost").split(":")[0]

    return render(
        request,
        "dashboard.html",
        {
            "stats": stats,
            "live_matches": live_matches,
            "twitch_embed": twitch_embed,
            "twitch_parent": twitch_parent,
            "open_tournaments": open_tournaments,
            "hall_of_fame": hall_of_fame,
            "activity": activity,
            **_role_flags(_read_current_user(request)),
        },
    )


def login_view(request):
    error_code = request.GET.get("error")
    if error_code:
        if error_code == "SteamIpNotSupported":
            messages.error(
                request,
                "Steam не поддерживает авторизацию через IP-адрес. Пожалуйста, используйте доменное имя (например, localhost или настройте локальный домен в hosts)."
            )
        elif error_code == "SteamAuthFailed":
            messages.error(request, "Не удалось авторизоваться через Steam. Повторите попытку.")
        elif error_code == "InvalidSteamId":
            messages.error(request, "Steam вернул пустой или неверный идентификатор пользователя.")
        else:
            messages.error(request, f"Ошибка авторизации: {error_code}")

    if request.method == "POST":
        form = LoginForm(request.POST)
        if form.is_valid():
            login_result = api_client.login(form.cleaned_data["email"], form.cleaned_data["password"])
            if login_result.ok:
                token = (login_result.data or {}).get("token")
                if token:
                    request.session["api_token"] = token
                    me_result = api_client.me(token)
                    request.session["current_user"] = me_result.data if me_result.ok else (login_result.data or {}).get("user", {})
                    response = redirect("dashboard")
                    response.set_cookie("api_token", token, max_age=8*3600, httponly=False, samesite="Lax")
                    return response
                form.add_error(None, "Сервер не вернул токен авторизации")
            else:
                form.add_error(None, (login_result.error or {}).get("message", "Ошибка входа"))
    else:
        form = LoginForm()

    return render(request, "login.html", {"form": form, **_role_flags(_read_current_user(request))})


def logout_view(request):
    request.session.flush()
    messages.info(request, "Вы вышли из системы")
    response = redirect("dashboard")
    response.delete_cookie("api_token")
    return response


def steam_callback(request):
    token = request.GET.get("token")
    if token:
        request.session["api_token"] = token
        me_result = api_client.me(token)
        if me_result.ok:
            request.session["current_user"] = me_result.data
            response = redirect("dashboard")
            response.set_cookie("api_token", token, max_age=8*3600, httponly=False, samesite="Lax")
            return response
        messages.error(request, "Не удалось получить профиль после авторизации Steam")
        return redirect("login")

    # Linking flow
    openid_params = request.GET.dict()
    if not openid_params:
        messages.error(request, "Ошибка Steam: пустой ответ")
        return redirect('profile')

    user = _read_current_user(request)
    if not user:
        messages.error(request, "Требуется авторизация")
        return redirect('login')

    user_id = user.get("id")
    api_token = request.session.get("api_token") or request.COOKIES.get("api_token")

    result = api_client.verify_steam_openid(user_id, openid_params, token=api_token)
    
    if result.ok:
        me_res = api_client.me(api_token)
        if me_res.ok:
            request.session["current_user"] = me_res.data
        messages.success(request, (result.data or {}).get("message", "Steam-аккаунт успешно привязан"))
    else:
        _add_api_error(request, result, "Не удалось привязать Steam-аккаунт")

    return redirect('profile')


def tournaments(request, is_pro=None):
    token = request.session.get("api_token")
    user = _read_current_user(request)
    roles = _role_flags(user)

    if request.method == "POST":
        action = request.POST.get("action")

        # Избранное требует только входа (без подтверждённой почты),
        # поэтому обрабатываем его до общего _require_auth.
        if action == "toggle_favorite":
            if not token:
                messages.info(request, "Войдите в аккаунт")
                return redirect("login")
            _handle_toggle_favorite(request, token)
            return redirect("pro_tournaments" if is_pro else "play_tournaments")

        auth_redirect = _require_auth(request, token)
        if auth_redirect:
            return auth_redirect

        if action == "create_tournament":
            if not roles["is_admin"]:
                messages.error(request, "Создавать турниры может только администратор")
                return redirect("pro_tournaments" if is_pro else "play_tournaments")
            form = TournamentCreateForm(request.POST)
            if form.is_valid():
                payload = {
                    "name": form.cleaned_data["name"],
                    "game": form.cleaned_data["game"],
                    "prizePool": float(form.cleaned_data["prize_pool"]),
                    "maxParticipants": form.cleaned_data["max_participants"],
                    "startDate": form.cleaned_data["start_date"].strftime("%Y-%m-%d"),
                    "format": form.cleaned_data["format"],
                    "stageType": form.cleaned_data["stage_type"],
                    "status": "planned",
                }
                create_result = api_client.create_tournament(token=token, payload=payload)
                if create_result.ok:
                    messages.success(request, "Турнир создан")
                    new_id = (create_result.data or {}).get("id")
                    return redirect("pro_tournament_detail" if is_pro else "play_tournament_detail", tournament_id=new_id) if new_id else redirect("pro_tournaments" if is_pro else "play_tournaments")
                _add_api_error(request, create_result, "Не удалось создать турнир")
            else:
                for errors in form.errors.values():
                    for error in errors:
                        messages.error(request, error)
            return redirect("pro_tournaments" if is_pro else "play_tournaments")

        if action == "delete_tournament":
            if not roles["is_admin"]:
                messages.error(request, "Удалять турниры может только администратор")
                return redirect("pro_tournaments" if is_pro else "play_tournaments")
            tournament_id = int(request.POST.get("tournament_id", "0") or 0)
            result = api_client.delete_tournament(tournament_id, token=token)
            if result.ok:
                messages.success(request, "Турнир удалён")
            else:
                _add_api_error(request, result, "Не удалось удалить турнир")
            return redirect("pro_tournaments" if is_pro else "play_tournaments")

    search_query = request.GET.get("search")
    tournaments_result = api_client.get_tournaments(token=token, search=search_query)
    all_t = tournaments_result.data or []
    if is_pro is not None:
        all_t = [t for t in all_t if bool(t.get("isExternal")) == is_pro]
    tournaments_list = [_normalize_tournament(item) for item in all_t]
    
    if not tournaments_result.ok:
        _add_api_error(request, tournaments_result, "Не удалось получить турниры")

    favorite_ids = _load_favorite_ids(token) if user else set()

    return render(
        request,
        "tournaments.html",
        {
            "tournaments": tournaments_list,
            "favorite_ids": favorite_ids,
            "create_tournament_form": TournamentCreateForm(),
            "is_pro": is_pro,
            **roles,
        },
    )


def teams(request):
    token = request.session.get("api_token")
    user = _read_current_user(request)
    roles = _role_flags(user)

    if request.method == "POST":
        action = request.POST.get("action")
        auth_redirect = _require_auth(request, token)
        if auth_redirect:
            return auth_redirect

        if action == "create_team":
            form = TeamCreateForm(request.POST)
            if form.is_valid():
                result = api_client.create_team(form.cleaned_data["name"], token=token)
                if result.ok:
                    messages.success(request, "Команда создана")
                else:
                    _add_api_error(request, result, "Не удалось создать команду")
            else:
                for errors in form.errors.values():
                    for error in errors:
                        messages.error(request, error)
            return redirect("play_teams")

        if action == "generate_bracket":
            tournament_id = int(request.POST.get("tournament_id", "0"))
            if tournament_id > 0:
                result = api_client.generate_tournament_bracket(tournament_id, token=token)
                if result.ok:
                    messages.success(request, "Сетка турнира успешно сгенерирована!")
                else:
                    _add_api_error(request, result, "Не удалось сгенерировать сетку")
            return redirect("play_teams") 

        if action == "add_player":
            team_id = int(request.POST.get("team_id", "0") or 0)
            nickname = (request.POST.get("nickname") or "").strip()
            rating = (request.POST.get("rating") or "").strip()
            game = (request.POST.get("game") or "counterstrike").strip()
            result = api_client.add_team_player(team_id, nickname, token=token, rating=rating, game=game)
            if result.ok:
                messages.success(request, "Игрок добавлен")
            else:
                _add_api_error(request, result, "Не удалось добавить игрока")
            return redirect("play_teams")

        if action == "confirm_rating":
            team_id = int(request.POST.get("team_id", "0") or 0)
            player_id = int(request.POST.get("player_id", "0") or 0)
            result = api_client.confirm_team_player_rating(team_id, player_id, token=token)
            if result.ok:
                messages.success(request, "Рейтинг подтверждён")
            else:
                _add_api_error(request, result, "Не удалось подтвердить рейтинг")
            return redirect("play_teams")

        if action == "delete_player":
            team_id = int(request.POST.get("team_id", "0") or 0)
            player_id = int(request.POST.get("player_id", "0") or 0)
            result = api_client.delete_team_player(team_id, player_id, token=token)
            if result.ok:
                messages.success(request, "Игрок удалён")
            else:
                _add_api_error(request, result, "Не удалось удалить игрока")
            return redirect("play_teams")

        if action == "delete_team":
            team_id = int(request.POST.get("team_id", "0") or 0)
            result = api_client.delete_team(team_id, token=token)
            if result.ok:
                messages.success(request, "Команда удалена")
            else:
                _add_api_error(request, result, "Не удалось удалить команду")
            return redirect("play_teams")

        if action == "create_vacancy":
            team_id = int(request.POST.get("team_id", "0") or 0)
            form = TeamVacancyForm(request.POST)
            if form.is_valid():
                result = api_client.create_vacancy(team_id, form.cleaned_data["required_role"], form.cleaned_data["description"], token=token)
                if result.ok:
                    messages.success(request, "Вакансия открыта")
                else:
                    _add_api_error(request, result, "Не удалось открыть вакансию")
            else:
                for errors in form.errors.values():
                    for error in errors:
                        messages.error(request, error)
            return redirect("play_teams")

        if action == "delete_vacancy":
            team_id = int(request.POST.get("team_id", "0") or 0)
            vacancy_id = int(request.POST.get("vacancy_id", "0") or 0)
            result = api_client.delete_vacancy(team_id, vacancy_id, token=token)
            if result.ok:
                messages.success(request, "Вакансия удалена")
            else:
                _add_api_error(request, result, "Не удалось удалить вакансию")
            return redirect("play_teams")

    teams_result = api_client.get_teams(token=token)
    teams_data = teams_result.data if teams_result.ok else []
    if not teams_result.ok:
        _add_api_error(request, teams_result, "Не удалось загрузить команды")

    return render(
        request,
        "teams.html",
        {"teams": teams_data, "team_form": TeamCreateForm(), "player_form": TeamPlayerForm(), "vacancy_form": TeamVacancyForm(), **roles},
    )


def tournament_detail(request, tournament_id: int):
    token = request.session.get("api_token")
    user = _read_current_user(request)
    roles = _role_flags(user)

    tournament_result = api_client.get_tournament(tournament_id, token=token)
    if not tournament_result.ok:
        _add_api_error(request, tournament_result, "Турнир не найден")
        return redirect("dashboard")
    tournament_payload = tournament_result.data or {}
    is_external = bool(tournament_payload.get("isExternal"))
    tournament = _normalize_tournament(tournament_payload)

    # Добавляем bracket_rounds в context и возвращаем render()
    if request.method == "POST":
        action = request.POST.get("action")

        if action == "apply_to_tournament":
            auth_redirect = _require_auth(request, token)
            if auth_redirect:
                return auth_redirect
            if not roles["is_captain"]:
                messages.error(request, "Подать заявку может только капитан команды")
                return redirect("pro_tournament_detail" if is_external else "play_tournament_detail", tournament_id=tournament_id)
            team_id = int(request.POST.get("team_id", "0") or 0)
            result = api_client.apply_to_tournament(tournament_id, team_id, token=token)
            if result.ok:
                messages.success(request, "Заявка отправлена")
            else:
                _add_api_error(request, result, "Не удалось отправить заявку")
            return redirect("pro_tournament_detail" if is_external else "play_tournament_detail", tournament_id=tournament_id)

        if action == "toggle_favorite":
            # Только вход, без проверки почты — в отличие от остальных действий
            if not token:
                messages.info(request, "Войдите в аккаунт")
                return redirect("login")
            _handle_toggle_favorite(request, token)
            return redirect("pro_tournament_detail" if is_external else "play_tournament_detail", tournament_id=tournament_id)

        if action in {"approve_application", "reject_application", "save_planning", "generate_bracket", "save_payouts", "set_status", "attach_stream", "distribute_prizes"}:
            auth_redirect = _require_auth(request, token)
            if auth_redirect:
                return auth_redirect
            if not (roles["is_admin"] or roles["is_judge"]):
                messages.error(request, "Недостаточно прав")
                return HttpResponseForbidden("Недостаточно прав")

        if action in {"approve_application", "reject_application"}:
            application_id = int(request.POST.get("application_id", "0") or 0)
            result = (
                api_client.approve_tournament_application(tournament_id, application_id, token=token)
                if action == "approve_application"
                else api_client.reject_tournament_application(tournament_id, application_id, token=token)
            )
            if result.ok:
                messages.success(request, "Заявка подтверждена" if action == "approve_application" else "Заявка отклонена")
            else:
                _add_api_error(request, result, "Ошибка обработки заявки")
            return redirect("pro_tournament_detail" if is_external else "play_tournament_detail", tournament_id=tournament_id)

        if action == "save_planning":
            result = api_client.save_tournament_planning(
                tournament_id,
                token=token,
                format_value=request.POST.get("format", "single_elimination"),
                stage_type=request.POST.get("stage_type", "single"),
            )
            if result.ok:
                messages.success(request, "Параметры сетки сохранены")
            else:
                _add_api_error(request, result, "Не удалось сохранить настройки сетки")
            return redirect("pro_tournament_detail" if is_external else "play_tournament_detail", tournament_id=tournament_id)

        if action == "save_payouts":
            payouts = []
            for idx in range(1, 4):
                place = (request.POST.get(f"place_{idx}") or "").strip()
                percent = (request.POST.get(f"percent_{idx}") or "").strip()
                if place and percent:
                    try:
                        payouts.append({"place": place, "percent": float(percent)})
                    except ValueError:
                        messages.error(request, f"Некорректный процент: {place}")
                        return redirect("pro_tournament_detail" if is_external else "play_tournament_detail", tournament_id=tournament_id)
            result = api_client.set_prize_payouts(tournament_id, token=token, payouts=payouts)
            if result.ok:
                messages.success(request, "Распределение обновлено")
            else:
                _add_api_error(request, result, "Не удалось сохранить распределение")
            return redirect("pro_tournament_detail" if is_external else "play_tournament_detail", tournament_id=tournament_id)

        if action == "set_status":
            status = request.POST.get("status", "planned")
            result = api_client.update_tournament_status(tournament_id, status=status, token=token)
            if result.ok:
                messages.success(request, "Статус турнира обновлён")
            else:
                _add_api_error(request, result, "Не удалось изменить статус")
            return redirect("pro_tournament_detail" if is_external else "play_tournament_detail", tournament_id=tournament_id)

        if action == "attach_stream":
            match_id = int(request.POST.get("match_id", "0") or 0)
            stream_url = (request.POST.get("stream_url") or "").strip()
            result = api_client.attach_match_stream(match_id, stream_url=stream_url, token=token)
            if result.ok:
                messages.success(request, "Стрим привязан к матчу")
            else:
                _add_api_error(request, result, "Не удалось привязать стрим")
            url = reverse("pro_tournament_detail" if is_external else "play_tournament_detail", kwargs={"tournament_id": tournament_id})
            return redirect(f"{url}?tab=streams")

        if action == "distribute_prizes":
            result = api_client.distribute_prizes(tournament_id, token=token)
            if result.ok:
                messages.success(request, "Призовой фонд распределён")
            else:
                _add_api_error(request, result, "Не удалось распределить призовые")
            url = reverse("pro_tournament_detail" if is_external else "play_tournament_detail", kwargs={"tournament_id": tournament_id})
            return redirect(f"{url}?tab=prize")

        if action == "generate_bracket":
            auth_redirect = _require_auth(request, token)
            if auth_redirect:
                return auth_redirect

            result = api_client.generate_tournament_bracket(tournament_id, token=token)
            if result.ok:
                messages.success(request, "Сетка турнира успешно сгенерирована!")
            else:
                _add_api_error(request, result, "Не удалось сгенерировать сетку. Проверьте, есть ли подтвержденные команды.")
            
            return redirect("pro_tournament_detail" if is_external else "play_tournament_detail", tournament_id=tournament_id)
            
        if action == "vote_mvp":
            auth_redirect = _require_auth(request, token)
            if auth_redirect:
                return auth_redirect
            player_id = int(request.POST.get("player_id", "0") or 0)
            result = api_client.vote_mvp(tournament_id, player_id, token=token)
            if result.ok:
                messages.success(request, "Голос принят")
            else:
                _add_api_error(request, result, "Ошибка голосования")
            url = reverse("pro_tournament_detail" if is_external else "play_tournament_detail", kwargs={"tournament_id": tournament_id})
            return redirect(f"{url}?tab=mvp")


    matches_result = api_client.get_matches(tournament_id, token=token)
    matches = [_normalize_match(item) for item in (matches_result.data or [])] if matches_result.ok else []

    my_teams = []
    my_apps = []
    all_apps = []
    if token and user:
        teams_result = api_client.get_teams(token=token)
        if teams_result.ok:
            email = (user.get("email") or "").lower()
            my_teams = [t for t in (teams_result.data or []) if (t.get("captainEmail") or "").lower() == email]
        my_apps_result = api_client.my_tournament_applications(tournament_id, token=token)
        if my_apps_result.ok:
            my_apps = my_apps_result.data or []
        if roles["is_admin"] or roles["is_judge"]:
            all_apps_result = api_client.list_tournament_applications(tournament_id, token=token)
            if all_apps_result.ok:
                all_apps = all_apps_result.data or []

    bracket_result = api_client.get_tournament_bracket(tournament_id, token=token)
    bracket = bracket_result.data if bracket_result.ok else {"groups": [], "matches": [], "summary": ""}
    # Группировка матчей по раундам для табличного рендера — без неё шаблон
    # никогда не показывал сетку (bracket_rounds в API-ответе нет)
    bracket["bracket_rounds"] = _group_bracket_rounds(bracket.get("matches") or [])

    prize_result = api_client.get_prize_pool(tournament_id, token=token)
    prize_pool = prize_result.data if prize_result.ok else {"totalAmount": tournament["prizePool"], "payouts": tournament.get("prizePayouts", [])}

    mvp_result = api_client.get_mvp(tournament_id, token=token)
    mvp_payload = _normalize_mvp_payload(mvp_result.data if mvp_result.ok else {"isOpen": False, "candidates": [], "results": []})

    # Аналитика строго по этому турниру (изоляция внешних данных от локальных)
    analytics_result = api_client.get_tournament_analytics(tournament_id, token=token)
    analytics_payload = analytics_result.data if analytics_result.ok else {
        "summary": {},
        "standings": [],
        "playerStats": [],
        "prizePools": [],
        "isExternal": tournament.get("isExternal", False),
    }

    streams_result = api_client.get_streams(token=token)
    streams_payload = streams_result.data if streams_result.ok else []
    active_tab = (request.GET.get("tab") or "overview").strip() or "overview"

    is_favorited = bool(user) and tournament_id in _load_favorite_ids(token)

    return render(
        request,
        "tournament_detail.html",
        {
            "tournament": tournament,
            "is_favorited": is_favorited,
            "matches": matches,
            "my_teams": my_teams,
            "my_apps": my_apps,
            "all_apps": all_apps,
            "bracket": bracket,
            "prize_pool": prize_pool,
            "mvp_payload": mvp_payload,
            "analytics_payload": analytics_payload,
            "streams_payload": streams_payload,
            "active_tab": active_tab,
            **roles,
        },
    )





def match_center(request, tournament_id: int):
    token = request.session.get("api_token")
    user = _read_current_user(request)
    roles = _role_flags(user)
    result_form = MatchResultForm(request.POST or None)

    tournament_result = api_client.get_tournament(tournament_id, token=token)
    tournament_payload = tournament_result.data or {}
    is_external = bool(tournament_payload.get("isExternal"))

    if request.method == "POST":
        if is_external:
            messages.info(request, "Матчи доступны только для просмотра")
            return redirect("pro_match_center" if is_external else "play_match_center", tournament_id=tournament_id)
        if not (roles["is_admin"] or roles["is_judge"]):
            messages.error(request, "Недостаточно прав")
            return HttpResponseForbidden("Недостаточно прав")
        if result_form.is_valid():
            update_result = api_client.update_match_result(
                result_form.cleaned_data["match_id"],
                result_form.cleaned_data["score_a"],
                result_form.cleaned_data["score_b"],
                token=token,
            )
            if update_result.ok:
                messages.success(request, "Результат обновлён")
            else:
                _add_api_error(request, update_result, "Ошибка обновления")
            return redirect("pro_match_center" if is_external else "play_match_center", tournament_id=tournament_id)

    matches_result = api_client.get_matches(tournament_id, token=token)
    matches = [_normalize_match(item) for item in (matches_result.data or [])] if matches_result.ok else []
    matches_readonly = is_external or any(str(m["id"]).startswith("local-") for m in matches)

    return render(
        request,
        "match.html",
        {
            "matches": matches,
            "form": result_form,
            "tournament_id": tournament_id,
            "matches_readonly": matches_readonly,
            **roles,
        },
    )


def voting(request):
    nominees_result = api_client.get_nominees()
    nominees = nominees_result.data if nominees_result.ok and nominees_result.data else []

    session_id = request.session.session_key
    has_voted = False
    if session_id:
        voted_data = api_client.has_voted(session_id)
        has_voted = voted_data.ok and (voted_data.data or {}).get("hasVoted", False)

    if request.method == "POST":
        if has_voted:
            messages.info(request, "Вы уже голосовали в этой сессии")
            return redirect("pro_voting")
        nominee_id = int(request.POST.get("nominee_id", "0"))
        vote_result = api_client.vote(nominee_id, session_id, request.META.get("REMOTE_ADDR", ""))
        if vote_result.ok and (vote_result.data or {}).get("success"):
            messages.success(request, (vote_result.data or {}).get("message", "Голос засчитан"))
            return redirect("pro_voting")
        _add_api_error(request, vote_result, "Ошибка голосования")

    return render(request, "voting.html", {"nominees": nominees, "has_voted": has_voted, **_role_flags(_read_current_user(request))})


def mvp(request, tournament_id: int):
    token = request.session.get("api_token")
    roles = _role_flags(_read_current_user(request))

    if request.method == "POST":
        auth_redirect = _require_auth(request, token)
        if auth_redirect:
            return auth_redirect

        player_id = int(request.POST.get("player_id", "0"))
        vote_result = api_client.vote_mvp(tournament_id, player_id, token=token)
        if vote_result.ok:
            messages.success(request, "Голос за MVP принят")
        else:
            _add_api_error(request, vote_result, "Ошибка голосования")

    mvp_result = api_client.get_mvp(tournament_id, token=token)
    payload = _normalize_mvp_payload(mvp_result.data if mvp_result.ok else {"isOpen": False, "candidates": [], "results": []})
    return render(request, "mvp.html", {"mvp": payload, "tournament_id": tournament_id, **roles})


def registration(request):
    if request.method == "POST":
        form = RegistrationForm(request.POST)
        if form.is_valid():
            register_result = api_client.register(
                form.cleaned_data["email"],
                form.cleaned_data["password"],
                form.cleaned_data["nickname"],
                form.cleaned_data["role"],
            )
            if register_result.ok:
                # Бэкенд при регистрации сам шлёт письмо подтверждения
                email_sent = bool((register_result.data or {}).get("verificationEmailSent"))
                login_result = api_client.login(form.cleaned_data["email"], form.cleaned_data["password"])
                if login_result.ok:
                    token = (login_result.data or {}).get("token")
                    if token:
                        request.session["api_token"] = token
                        me_result = api_client.me(token)
                        request.session["current_user"] = me_result.data if me_result.ok else (login_result.data or {}).get("user", {})
                    response = redirect("dashboard")
                    if token:
                        response.set_cookie("api_token", token, max_age=8*3600, httponly=False, samesite="Lax")
                    return response
                return redirect("login")
            form.add_error(None, (register_result.error or {}).get("message", "Ошибка регистрации"))
    else:
        form = RegistrationForm()

    return render(request, "registration.html", {"form": form, **_role_flags(_read_current_user(request))})


def profile(request):
    user = _read_current_user(request)
    if not user:
        messages.info(request, "Войдите, чтобы открыть профиль")
        return redirect("login")

    token = request.session.get("api_token")
    roles = _role_flags(user)
    user_id = user.get("id")
    edit_form = ProfileEditForm(initial={
        "nickname": user.get("nickname", ""),
    })
    faceit_form = FaceitVerifyForm()

    if request.method == "POST":
        action = request.POST.get("action")

        if action == "respond_invite":
            invite_id = int(request.POST.get("invite_id", "0"))
            response_action = request.POST.get("response_action", "")
            result = api_client.respond_invite(invite_id, response_action, token=token)
            if result.ok:
                messages.success(request, result.data.get("message", "Успешно"))
            else:
                _add_api_error(request, result, "Ошибка при ответе на приглашение")
            return redirect("profile")

        if action == "save_profile":
            edit_form = ProfileEditForm(request.POST)
            if edit_form.is_valid():
                result = api_client.update_profile(
                    nickname=edit_form.cleaned_data["nickname"],
                    bio=user.get("bio") or "",
                    token=token,
                    game_role=user.get("gameRole", ""),
                    availability=user.get("availability", ""),
                    pitch=user.get("pitch") or "",
                    discord_id=user.get("discordId") or "",
                    highlights_url=user.get("highlightsUrl") or "",
                    country=user.get("country") or "",
                    city=user.get("city") or "",
                    languages=user.get("languages") or "",
                )
                if result.ok:
                    request.session["current_user"] = result.data
                    messages.success(request, "Профиль обновлён")
                    return redirect("profile")
                _add_api_error(request, result, "Не удалось обновить профиль")

        elif action == "link_faceit_oauth":
            redirect_uri = request.build_absolute_uri('/profile/faceit/callback')
            result = api_client.get_faceit_oauth_url(redirect_uri)
            if result.ok and result.data and "url" in result.data:
                return redirect(result.data["url"])
            _add_api_error(request, result, "Не удалось получить ссылку для Faceit OAuth")

        elif action == "link_steam_openid":
            redirect_uri = request.build_absolute_uri('/profile/steam/callback')
            result = api_client.get_steam_openid_url(redirect_uri)
            if result.ok and result.data and "url" in result.data:
                return redirect(result.data["url"])
            _add_api_error(request, result, "Не удалось получить ссылку для Steam OpenID")

        elif action == "unlink_faceit":
            result = api_client.unlink_faceit(user_id, token=token)
            if result.ok:
                me_res = api_client.me(token)
                if me_res.ok:
                    request.session["current_user"] = me_res.data
                messages.success(request, "Faceit-аккаунт отвязан")
                return redirect("profile")
            _add_api_error(request, result, "Не удалось отвязать аккаунт")

        elif action == "send_verification_email":
            result = api_client.send_email_verification(user_id, token)
            if result.ok:
                messages.success(request, "Письмо со ссылкой отправлено")
            else:
                _add_api_error(request, result, "Ошибка отправки письма")
            return redirect("profile")

        elif action == "toggle_lft":
            enabled = (request.POST.get("enabled") or "0") == "1"
            result = api_client.set_looking_for_team(enabled, token=token)
            if result.ok:
                # Сбрасываем кэш профиля — изменился флаг isLookingForTeam
                request.session.pop("current_user", None)
                messages.success(request, (result.data or {}).get("message", "Статус обновлён"))
            else:
                _add_api_error(request, result, "Не удалось обновить статус поиска команды")
            return redirect("profile")

        elif action == "verify_rating":
            provider = request.POST.get("provider", "")
            if provider == "faceit" and user.get("faceitProfileUrl"):
                result = api_client.verify_rating(provider="faceit", profile_url=user.get("faceitProfileUrl"), token=token)
            elif provider == "steam" and user.get("steamId"):
                result = api_client.verify_rating(provider="steam", profile_url=f"https://steamcommunity.com/profiles/{user.get('steamId')}", token=token)
            else:
                messages.error(request, "Не указан провайдер или аккаунт не привязан")
                return redirect("profile")
            
            if result.ok:
                request.session["current_user"] = result.data.get("profile")
                messages.success(request, "Рейтинг подтвержден!")
            else:
                _add_api_error(request, result, "Не удалось подтвердить рейтинг")
            return redirect("profile")

    selected_game = (request.GET.get("game") or "counterstrike").strip() or "counterstrike"
    nickname = (user.get("nickname") or "").strip()
    esports_payload = None
    esports_results = []
    esports_error = None

    if nickname:
        result = api_client.esports_player(nickname, game=selected_game)
        if result.ok:
            esports_payload = result.data or {}
            esports_results = esports_payload.get("results") or []
            if not esports_results:
                esports_error = "Игрок не найден"
        else:
            esports_error = (result.error or {}).get("message", "Не удалось получить данные игрока")
    else:
        esports_error = "У аккаунта не задан ник"

    # История рейтинга для графика динамики (Chart.js)
    history_result = api_client.get_rating_history(token)
    rating_history = (history_result.data or []) if history_result.ok else []

    context = {
        "selected_game": selected_game,
        "esports_payload": esports_payload,
        "esports_results": esports_results,
        "esports_error": esports_error,
        "edit_form": edit_form,
        "faceit_form": faceit_form,
        "rating_history": rating_history,
        **roles,
    }
    return render(request, "profile.html", context)


def faceit_callback(request):
    code = request.GET.get('code')
    if not code:
        messages.error(request, "Ошибка Faceit: отсутствует код авторизации")
        return redirect('profile')

    user = _read_current_user(request)
    if not user:
        messages.error(request, "Требуется авторизация")
        return redirect('login')

    user_id = user.get("id")
    token = request.session.get("api_token") or request.COOKIES.get("api_token")
    redirect_uri = request.build_absolute_uri('/profile/faceit/callback')

    # Remove query string from redirect_uri if any, as build_absolute_uri keeps it
    redirect_uri = redirect_uri.split('?')[0]

    result = api_client.verify_faceit_oauth(user_id, code, redirect_uri, token=token)
    
    if result.ok:
        # Обновляем профиль в сессии
        me_res = api_client.me(token)
        if me_res.ok:
            request.session["current_user"] = me_res.data
        messages.success(request, (result.data or {}).get("message", "Faceit-аккаунт успешно привязан"))
    else:
        _add_api_error(request, result, "Не удалось привязать Faceit-аккаунт")

    return redirect('profile')

# steam_callback has been merged with the SSO login steam_callback view above.

@login_required
def smart_scouting(request, team_id: int):
    # Fetch team from backend (assumes an API endpoint exists, or just pass team_id)
    # Get first recommendation
    token = request.session.get("api_token") or request.COOKIES.get("api_token")
    
    res = api_client.get_smart_scouting_recommendations(team_id, token=token)
    recommendation = None
    if res.ok and res.data:
        recommendation = res.data[0] if isinstance(res.data, list) and res.data else None
            
    return render(request, "smart_scouting.html", {
        "team_id": team_id,
        "recommendation": recommendation
    })

@login_required
def smart_scouting_swipe(request, team_id: int):
    if request.method == "POST":
        player_id = request.POST.get("player_id")
        action = request.POST.get("action")
        
        token = request.session.get("api_token") or request.COOKIES.get("api_token")
        
        api_client.swipe_smart_scouting(team_id, int(player_id), action, token=token)
        
        # Fetch next recommendation
        res = api_client.get_smart_scouting_recommendations(team_id, token=token)
        recommendation = None
        if res.ok and res.data:
            recommendation = res.data[0] if isinstance(res.data, list) and res.data else None
                
        return render(request, "partials/scouting_card.html", {
            "team_id": team_id,
            "recommendation": recommendation
        })
    return HttpResponse(status=400)

def fantasy_draft(request, tournament_id: int):
    token = request.session.get("api_token") or request.COOKIES.get("api_token")
    res = api_client.get_tournament(tournament_id, token=token)
    tournament = res.data if (res.ok and res.data) else {"id": tournament_id, "name": f"Турнир #{tournament_id}"}
    
    players = []
    res_p = api_client.get_fantasy_players(tournament_id, token=token)
    if res_p.ok and res_p.data:
        players = res_p.data

    # Load session draft state
    draft = request.session.get(f"draft_{tournament_id}", [])
    # Verify that drafted players still exist in the pool
    valid_player_ids = {p.get("id") for p in players}
    draft = [pid for pid in draft if pid in valid_player_ids]
    request.session[f"draft_{tournament_id}"] = draft

    roster_players = [p for p in players if p.get("id") in draft]
    total_cost = sum(p.get("cost", 0) for p in roster_players)

    return render(request, "fantasy_draft.html", {
        "tournament": tournament,
        "players": players,
        "roster_players": roster_players,
        "draft_ids": draft,
        "budget_remaining": 500 - total_cost,
        "draft_count": len(draft)
    })

@login_required
def fantasy_draft_toggle(request, tournament_id: int, player_id: int):
    user = _read_current_user(request)
    if not user or not user.get("isEmailVerified"):
        return HttpResponseForbidden("Пожалуйста, подтвердите вашу почту, чтобы участвовать в Fantasy Draft.")
        
    token = request.session.get("api_token") or request.COOKIES.get("api_token")
    draft = request.session.get(f"draft_{tournament_id}", [])
    
    players_res = api_client.get_fantasy_players(tournament_id, token=token)
    players = players_res.data or []
    player = next((p for p in players if p.get("id") == player_id), None)
    
    error_message = None
    if not player:
        error_message = "Игрок не найден."
    else:
        cost = player.get("cost", 0)
        if player_id in draft:
            draft.remove(player_id)
        else:
            if len(draft) >= 5:
                error_message = "Вы не можете выбрать более 5 игроков."
            else:
                current_cost = sum(p.get("cost", 0) for p in players if p.get("id") in draft)
                if current_cost + cost > 500:
                    error_message = f"Недостаточно бюджета! Лимит 500$, текущая стоимость с учетом игрока: {current_cost + cost}$"
                else:
                    draft.append(player_id)
                    
    request.session[f"draft_{tournament_id}"] = draft
    
    roster_players = [p for p in players if p.get("id") in draft]
    total_cost = sum(p.get("cost", 0) for p in roster_players)
    
    res = api_client.get_tournament(tournament_id, token=token)
    tournament = res.data if (res.ok and res.data) else {"id": tournament_id, "name": f"Турнир #{tournament_id}"}
    
    return render(request, "partials/fantasy_draft_inner.html", {
        "tournament": tournament,
        "players": players,
        "roster_players": roster_players,
        "draft_ids": draft,
        "budget_remaining": 500 - total_cost,
        "draft_count": len(draft),
        "error_message": error_message
    })

@login_required
def fantasy_draft_submit(request, tournament_id: int):
    user = _read_current_user(request)
    if not user or not user.get("isEmailVerified"):
        return HttpResponseForbidden("Пожалуйста, подтвердите вашу почту, чтобы участвовать в Fantasy Draft.")
        
    if request.method == "POST":
        team_name = request.POST.get("team_name", "").strip()
        draft = request.session.get(f"draft_{tournament_id}", [])
        token = request.session.get("api_token") or request.COOKIES.get("api_token")
        
        res = api_client.submit_fantasy_draft(
            tournament_id=tournament_id,
            team_name=team_name,
            player_ids=draft,
            token=token
        )
        
        players_res = api_client.get_fantasy_players(tournament_id, token=token)
        players = players_res.data or []
        roster_players = [p for p in players if p.get("id") in draft]
        total_cost = sum(p.get("cost", 0) for p in roster_players)
        
        success_message = None
        error_message = None
        if res.ok:
            success_message = "Ваш состав успешно зарегистрирован!"
        else:
            error_message = (res.error or {}).get('message', 'Ошибка при сохранении состава')
            
        res_t = api_client.get_tournament(tournament_id, token=token)
        tournament = res_t.data if (res_t.ok and res_t.data) else {"id": tournament_id, "name": f"Турнир #{tournament_id}"}
        
        return render(request, "partials/fantasy_draft_inner.html", {
            "tournament": tournament,
            "players": players,
            "roster_players": roster_players,
            "draft_ids": draft,
            "budget_remaining": 500 - total_cost,
            "draft_count": len(draft),
            "success_message": success_message,
            "error_message": error_message,
            "team_name": team_name
        })
    return HttpResponse(status=400)

@login_required
def fantasy_leaderboard(request, tournament_id: int):
    token = request.session.get("api_token") or request.COOKIES.get("api_token")
    leaderboard = []
    res = api_client.get_fantasy_leaderboard(tournament_id, token=token)
    if res.ok and res.data:
        leaderboard = res.data
    
    return render(request, "partials/fantasy_leaderboard.html", {
        "leaderboard": leaderboard
    })

@login_required
def teams_list(request):
    roles = _role_flags(_read_current_user(request))
    tournament_payload = None
    player_payload = None
    tournament_query = ""
    player_query = ""

def leaderboard(request):
    return render(request, "leaderboard.html")

def streams(request):
    roles = _role_flags(_read_current_user(request))
    tournament_payload = None
    player_payload = None
    tournament_query = ""
    player_query = ""
    diagnostics = None
    host = (request.get_host() or "localhost").split(":")[0]
    if host == "127.0.0.1":
        host = "localhost"

    if request.method == "POST":
        action = request.POST.get("action")
        if action == "find_tournament_streams":
            tournament_query = (request.POST.get("tournament_query") or "").strip()
            if tournament_query:
                result = api_client.esports_tournament_streams(tournament_query)
                if result.ok:
                    payload = result.data or {}
                    streams_prepared = []
                    for stream in payload.get("streams", []) or []:
                        url = stream.get("url") or ""
                        provider = stream.get("provider") or _detect_stream_provider(url)
                        streams_prepared.append({
                            "provider": provider,
                            "url": url,
                            "channel": stream.get("channel") or (_extract_twitch_channel(url) if provider.lower() == "twitch" else ""),
                            "embed_url": _build_youtube_embed(url) if provider.lower() == "youtube" else url,
                            "matchName": stream.get("matchName") or "",
                        })
                    payload["streams_prepared"] = streams_prepared
                    tournament_payload = payload
                else:
                    _add_api_error(request, result, "Не удалось получить стримы")
                    diagnostics = api_client.esports_diagnostics().data
            else:
                messages.error(request, "Введите название турнира")
        elif action == "find_player":
            player_query = (request.POST.get("player_query") or "").strip()
            if player_query:
                result = api_client.esports_player(player_query)
                if result.ok:
                    player_payload = result.data or {}
                else:
                    _add_api_error(request, result, "Игрок не найден")
                    diagnostics = api_client.esports_diagnostics().data
            else:
                messages.error(request, "Введите ник игрока")

    return render(
        request,
        "streams.html",
        {
            "tournament_payload": tournament_payload,
            "player_payload": player_payload,
            "tournament_query": tournament_query,
            "player_query": player_query,
            "twitch_parent": host,
            "diagnostics": diagnostics,
            **roles,
        },
    )


def analytics(request):
    token = request.session.get("api_token")
    game = request.GET.get("game") or None
    
    result = api_client.get_analytics(game=game, token=token)
    payload = result.data if result.ok else {"playerStats": [], "disciplinePopularity": [], "prizePools": [], "summary": {}}
    if not result.ok:
        messages.info(request, (result.error or {}).get("message", "Аналитика временно недоступна"))

    # Винрейты команд: общий / группы / плей-офф / упорные матчи
    winrates_result = api_client.get_team_winrates(game=game, token=token)
    team_winrates = (winrates_result.data or []) if winrates_result.ok else []

    # Зал славы (Hall of Fame)
    hof_result = api_client.get_hall_of_fame()
    hall_of_fame = (hof_result.data or []) if hof_result.ok else []

    return render(
        request,
        "analytics.html",
        {
            "analytics": payload,
            "team_winrates": team_winrates,
            "hall_of_fame": hall_of_fame,
            "selected_game": game,
            **_role_flags(_read_current_user(request)),
        },
    )


def scouting(request):
    """Доска скаутинга: свободные агенты (LFT), отсортированные по Faceit Elo."""
    token = request.session.get("api_token")
    user = _read_current_user(request)
    roles = _role_flags(user)
    scouting_form = None

    if user:
        scouting_form = ScoutingEditForm(initial={
            "game_role": user.get("gameRole", ""),
            "availability": user.get("availability", ""),
            "pitch": user.get("pitch", ""),
            "discord_id": user.get("discordId", ""),
            "country": user.get("country", ""),
            "city": user.get("city", ""),
            "languages": user.get("languages", ""),
            "bio": user.get("bio", ""),
            "highlights_url": user.get("highlightsUrl", "")
        })

    if request.method == "POST":
        action = request.POST.get("action")

        if action == "save_scouting_profile":
            if not token:
                messages.info(request, "Войдите в аккаунт")
                return redirect("login")
            
            form = ScoutingEditForm(request.POST)
            if form.is_valid():
                result = api_client.update_profile(
                    nickname=user.get("nickname", ""),
                    bio=form.cleaned_data.get("bio") or "",
                    token=token,
                    game_role=form.cleaned_data.get("game_role") or "",
                    availability=form.cleaned_data.get("availability") or "",
                    pitch=form.cleaned_data.get("pitch") or "",
                    discord_id=form.cleaned_data.get("discord_id") or "",
                    highlights_url=form.cleaned_data.get("highlights_url") or "",
                    country=form.cleaned_data.get("country") or "",
                    city=form.cleaned_data.get("city") or "",
                    languages=form.cleaned_data.get("languages") or "",
                )
                if result.ok:
                    request.session["current_user"] = result.data
                    messages.success(request, "Анкета скаутинга обновлена")
                else:
                    _add_api_error(request, result, "Не удалось обновить анкету")
            return redirect("play_scouting")

        if action == "toggle_lft":
            if not token:
                messages.info(request, "Войдите в аккаунт")
                return redirect("login")
            enabled = (request.POST.get("enabled") or "0") == "1"
            result = api_client.set_looking_for_team(enabled, token=token)
            if result.ok:
                # Профиль в сессии устарел — флаг isLookingForTeam изменился
                request.session.pop("current_user", None)
                messages.success(request, (result.data or {}).get("message", "Статус обновлён"))
            else:
                _add_api_error(request, result, "Не удалось обновить статус поиска команды")
            return redirect("play_scouting")
        
        elif action == "invite_player":
            if not token:
                messages.info(request, "Войдите в аккаунт")
                return redirect("login")
            team_id = int(request.POST.get("team_id", "0") or 0)
            nickname = (request.POST.get("nickname") or "").strip()
            if team_id > 0 and nickname:
                result = api_client.add_team_player(team_id, nickname, token=token)
                if result.ok:
                    messages.success(request, f"Игрок {nickname} приглашён в команду")
                else:
                    _add_api_error(request, result, f"Не удалось пригласить {nickname}")
            return redirect("play_scouting")

    agents_result = api_client.get_free_agents(token=token)
    free_agents = (agents_result.data or []) if agents_result.ok else []
    if not agents_result.ok:
        _add_api_error(request, agents_result, "Не удалось загрузить доску скаутинга")

    for agent in free_agents:
        agent["since_label"] = _humanize_time(agent.get("lookingForTeamSinceUtc"))

    my_teams = []
    if token and user:
        teams_result = api_client.get_teams(token=token)
        if teams_result.ok:
            email = (user.get("email") or "").lower()
            my_teams = [t for t in (teams_result.data or []) if (t.get("captainEmail") or "").lower() == email]

    vacancies_res = api_client.get_vacancies()
    vacancies = vacancies_res.data if vacancies_res.ok else []

    return render(
        request,
        "scouting.html",
        {
            "free_agents": free_agents,
            "vacancies": vacancies,
            "my_teams": my_teams,
            "scouting_form": scouting_form,
            **roles,
        },
    )


def scouting_agent_detail(request, agent_id: int):
    token = request.session.get("api_token")
    result = api_client.get_free_agent(agent_id)
    if not result.ok:
        return HttpResponse("Игрок не найден или ошибка API", status=404)
    
    agent = result.data
    user = _read_current_user(request)
    roles = _role_flags(user)
    
    my_teams = []
    if token and user:
        teams_result = api_client.get_teams(token=token)
        if teams_result.ok:
            email = (user.get("email") or "").lower()
            my_teams = [t for t in (teams_result.data or []) if (t.get("captainEmail") or "").lower() == email]

    return render(request, "partials/scouting_agent_detail.html", {
        "agent": agent,
        "current_user": user,
        "my_teams": my_teams,
        **roles,
    })


def predict_match(request, match_id: int):
    user = _read_current_user(request)
    if not user:
        return HttpResponseForbidden("Войдите в аккаунт, чтобы делать прогнозы.")
    if not user.get("isEmailVerified"):
        return HttpResponseForbidden("Пожалуйста, подтвердите вашу почту, чтобы делать прогнозы.")

    if request.method == "POST":
        token = request.session.get("api_token") or request.COOKIES.get("api_token")
        if not token:
            return HttpResponseForbidden("Пользователь не авторизован")

        predicted_team_id = request.POST.get("predicted_team_id")
        if not predicted_team_id:
            try:
                import json
                data = json.loads(request.body)
                predicted_team_id = data.get("predicted_team_id")
            except Exception:
                pass

        if not predicted_team_id:
            return HttpResponse("Не указана прогнозируемая команда", status=400)

        try:
            predicted_team_id = int(predicted_team_id)
        except ValueError:
            return HttpResponse("Некорректный ID команды", status=400)

        result = api_client.predict_match(match_id, predicted_team_id, token)
        if result.ok:
            html_content = (result.data or {}).get("raw", "")
            return HttpResponse(html_content, content_type="text/html")
        else:
            msg = result.error.get("message") if result.error else "Ошибка при отправке прогноза"
            return HttpResponse(f"<span class='text-danger small'>{msg}</span>", status=400)

    return HttpResponse("Метод не поддерживается", status=405)
