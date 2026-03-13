from datetime import date

from django.contrib import messages
from django.http import HttpResponseForbidden
from django.shortcuts import redirect, render

from .api_client import api_client
from .forms import LoginForm, MatchResultForm, RegistrationForm, TeamCreateForm, TeamPlayerForm

ROLE_ADMIN = "admin"
ROLE_JUDGE = "judge"
ROLE_CAPTAIN = "captain"
ROLE_VIEWER = "viewer"
STATUS_LABELS = {
    "planned": "Запланирован",
    "live": "Идёт",
    "finished": "Завершён",
    "approved": "Подтверждён",
    "pending": "На рассмотрении",
    "rejected": "Отклонена",
}


def _normalize_status(value):
    key = (value or "planned").lower()
    return key, STATUS_LABELS.get(key, value or "Запланирован")


def _normalize_tournament(item):
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
    status_key, status_label = _normalize_status(item.get("status", "planned"))
    return {
        "id": item.get("id"),
        "teamA": item.get("teamA", "н/д"),
        "teamB": item.get("teamB", "н/д"),
        "scoreA": item.get("scoreA", 0),
        "scoreB": item.get("scoreB", 0),
        "status": status_key,
        "status_label": status_label,
        "round": item.get("round", "н/д"),
        "groupName": item.get("groupName", ""),
        "streamUrl": item.get("streamUrl", ""),
    }




def _detect_stream_provider(url: str) -> str:
    u = (url or "").lower()
    if "twitch.tv" in u:
        return "twitch"
    if "youtube.com" in u or "youtu.be" in u:
        return "youtube"
    return "stream"


def _extract_twitch_channel(url: str) -> str:
    try:
        from urllib.parse import urlparse
        p = urlparse(url)
        parts = [x for x in p.path.split("/") if x]
        return parts[0] if parts else ""
    except Exception:
        return ""


def _build_youtube_embed(url: str) -> str:
    try:
        from urllib.parse import parse_qs, urlparse
        parsed = urlparse(url)
        if parsed.netloc.endswith('youtu.be'):
            video_id = parsed.path.strip('/')
        else:
            video_id = parse_qs(parsed.query).get('v', [''])[0]
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
    if cached_user:
        return cached_user

    me_result = api_client.me(token)
    if not me_result.ok:
        request.session.pop("api_token", None)
        request.session.pop("current_user", None)
        return None

    request.session["current_user"] = me_result.data
    return me_result.data


def _role_flags(user):
    role = (user or {}).get("role", "guest")
    return {
        "is_admin": role == ROLE_ADMIN,
        "is_judge": role == ROLE_JUDGE,
        "is_captain": role == ROLE_CAPTAIN,
        "is_viewer": role == ROLE_VIEWER,
        "current_user": user,
    }


def _process_result_error(request, result, *, clear_session_on_401=True):
    if result.ok:
        return None
    code = (result.error or {}).get("code")
    message = (result.error or {}).get("message", "Ошибка API")
    if code == "unauthorized":
        if clear_session_on_401:
            request.session.pop("api_token", None)
            request.session.pop("current_user", None)
        messages.info(request, "Сессия истекла. Войдите заново.")
        return redirect("login")
    if code == "forbidden":
        messages.error(request, message)
        return HttpResponseForbidden(message)
    messages.error(request, message)
    return None


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
        "live_streams": payload.get("liveStreams", 0),
        "viewers_estimated": payload.get("viewersEstimated", True),
        "live_tournaments": payload.get("liveTournaments", []) or [],
    }
    if not stats_result.ok:
        messages.info(request, (stats_result.error or {}).get("message", "API недоступно"))
    context = {"stats": stats}
    context.update(_role_flags(_read_current_user(request)))
    return render(request, "dashboard.html", context)


def login_view(request):
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
                    messages.success(request, "Вход выполнен")
                    return redirect("dashboard")
                form.add_error(None, "API вернул некорректный токен")
            else:
                form.add_error(None, (login_result.error or {}).get("message", "Ошибка входа"))
    else:
        form = LoginForm()

    return render(request, "login.html", {"form": form, **_role_flags(_read_current_user(request))})


def logout_view(request):
    request.session.flush()
    messages.info(request, "Вы вышли из системы")
    return redirect("dashboard")


def tournaments(request):
    token = request.session.get("api_token")
    tournaments_result = api_client.get_tournaments(token=token)
    tournaments_data = tournaments_result.data or []
    tournaments_list = [_normalize_tournament(item) for item in tournaments_data]
    if not tournaments_result.ok:
        messages.error(request, (tournaments_result.error or {}).get("message", "Не удалось получить турниры"))
    return render(request, "tournaments.html", {"tournaments": tournaments_list, **_role_flags(_read_current_user(request))})


def teams(request):
    token = request.session.get("api_token")
    user = _read_current_user(request)
    roles = _role_flags(user)

    if request.method == "POST":
        action = request.POST.get("action")
        if not token:
            messages.info(request, "Войдите, чтобы управлять командами")
            return redirect("login")

        if action == "create_team":
            form = TeamCreateForm(request.POST)
            if form.is_valid():
                result = api_client.create_team(form.cleaned_data["name"], token=token)
                if result.ok:
                    messages.success(request, "Команда создана")
                else:
                    messages.error(request, (result.error or {}).get("message", "Не удалось создать команду"))
            else:
                for errors in form.errors.values():
                    for error in errors:
                        messages.error(request, error)
            return redirect("teams")

        if action == "add_player":
            team_id = int(request.POST.get("team_id", "0") or 0)
            nickname = (request.POST.get("nickname") or "").strip()
            rating = (request.POST.get("rating") or "").strip()
            game = (request.POST.get("game") or "counterstrike").strip()
            result = api_client.add_team_player(team_id, nickname, token=token, rating=rating, game=game)
            if result.ok:
                messages.success(request, "Игрок добавлен")
            else:
                messages.error(request, (result.error or {}).get("message", "Не удалось добавить игрока"))
            return redirect("teams")

        if action == "confirm_rating":
            team_id = int(request.POST.get("team_id", "0") or 0)
            player_id = int(request.POST.get("player_id", "0") or 0)
            result = api_client.confirm_team_player_rating(team_id, player_id, token=token)
            if result.ok:
                messages.success(request, "Рейтинг подтверждён")
            else:
                messages.error(request, (result.error or {}).get("message", "Не удалось подтвердить рейтинг"))
            return redirect("teams")

        if action == "delete_player":
            team_id = int(request.POST.get("team_id", "0") or 0)
            player_id = int(request.POST.get("player_id", "0") or 0)
            result = api_client.delete_team_player(team_id, player_id, token=token)
            if result.ok:
                messages.success(request, "Игрок удалён")
            else:
                messages.error(request, (result.error or {}).get("message", "Не удалось удалить игрока"))
            return redirect("teams")

        if action == "delete_team":
            team_id = int(request.POST.get("team_id", "0") or 0)
            result = api_client.delete_team(team_id, token=token)
            if result.ok:
                messages.success(request, "Команда удалена")
            else:
                messages.error(request, (result.error or {}).get("message", "Не удалось удалить команду"))
            return redirect("teams")

    teams_result = api_client.get_teams(token=token)
    teams_data = teams_result.data if teams_result.ok else []
    if not teams_result.ok:
        messages.error(request, (teams_result.error or {}).get("message", "Не удалось загрузить команды"))

    return render(request, "teams.html", {"teams": teams_data, "team_form": TeamCreateForm(), "player_form": TeamPlayerForm(), **roles})


def tournament_detail(request, tournament_id: int):
    token = request.session.get("api_token")
    user = _read_current_user(request)
    roles = _role_flags(user)

    if request.method == "POST":
        action = request.POST.get("action")
        if action == "apply_to_tournament":
            if not token:
                messages.info(request, "Войдите, чтобы подать заявку.")
                return redirect("login")
            if not roles["is_captain"]:
                messages.error(request, "Подать заявку может только капитан команды.")
                return redirect("tournament_detail", tournament_id=tournament_id)
            team_id = int(request.POST.get("team_id", "0") or 0)
            result = api_client.apply_to_tournament(tournament_id, team_id, token=token)
            if result.ok:
                messages.success(request, "Заявка отправлена")
            else:
                messages.error(request, (result.error or {}).get("message", "Не удалось отправить заявку"))
            return redirect("tournament_detail", tournament_id=tournament_id)

        if action in {"approve_application", "reject_application"}:
            application_id = int(request.POST.get("application_id", "0") or 0)
            if action == "approve_application":
                result = api_client.approve_tournament_application(tournament_id, application_id, token=token)
                success_text = "Заявка подтверждена"
            else:
                result = api_client.reject_tournament_application(tournament_id, application_id, token=token)
                success_text = "Заявка отклонена"
            if result.ok:
                messages.success(request, success_text)
            else:
                messages.error(request, (result.error or {}).get("message", "Ошибка обработки заявки"))
            return redirect("tournament_detail", tournament_id=tournament_id)

        if action == "save_planning":
            format_value = request.POST.get("format", "single_elimination")
            stage_type = request.POST.get("stage_type", "single")
            result = api_client.save_tournament_planning(tournament_id, token=token, format_value=format_value, stage_type=stage_type)
            if result.ok:
                messages.success(request, "Параметры сетки сохранены")
            else:
                messages.error(request, (result.error or {}).get("message", "Не удалось сохранить настройки сетки"))
            return redirect("tournament_detail", tournament_id=tournament_id)

        if action == "save_payouts":
            payouts = []
            for idx in range(1, 4):
                place = (request.POST.get(f"place_{idx}") or "").strip()
                percent = (request.POST.get(f"percent_{idx}") or "").strip()
                if place and percent:
                    try:
                        payouts.append({"place": place, "percent": float(percent)})
                    except ValueError:
                        messages.error(request, f"Некорректный процент для {place}")
                        return redirect("tournament_detail", tournament_id=tournament_id)
            result = api_client.set_prize_payouts(tournament_id, token=token, payouts=payouts)
            if result.ok:
                messages.success(request, "Распределение призового фонда обновлено")
            else:
                messages.error(request, (result.error or {}).get("message", "Не удалось сохранить распределение"))
            return redirect("tournament_detail", tournament_id=tournament_id)

    tournament_result = api_client.get_tournament(tournament_id, token=token)
    if not tournament_result.ok:
        messages.error(request, (tournament_result.error or {}).get("message", "Турнир не найден"))
        return redirect("tournaments")

    tournament = _normalize_tournament(tournament_result.data or {})
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

    prize_result = api_client.get_prize_pool(tournament_id, token=token)
    prize_pool = prize_result.data if prize_result.ok else {"totalAmount": tournament["prizePool"], "payouts": tournament.get("prizePayouts", [])}

    return render(
        request,
        "tournament_detail.html",
        {
            "tournament": tournament,
            "matches": matches,
            "my_teams": my_teams,
            "my_apps": my_apps,
            "all_apps": all_apps,
            "bracket": bracket,
            "prize_pool": prize_pool,
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
            messages.info(request, "Матчи из PandaScore доступны только для просмотра.")
            return redirect("match_center", tournament_id=tournament_id)

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
                messages.error(request, (update_result.error or {}).get("message", "Ошибка обновления"))
            return redirect("match_center", tournament_id=tournament_id)

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
            return redirect("voting")
        nominee_id = int(request.POST.get("nominee_id", "0"))
        vote_result = api_client.vote(nominee_id, session_id, request.META.get("REMOTE_ADDR", ""))
        if vote_result.ok and (vote_result.data or {}).get("success"):
            messages.success(request, (vote_result.data or {}).get("message", "Голос засчитан"))
            return redirect("voting")
        messages.error(request, (vote_result.error or {}).get("message", "Ошибка голосования"))

    return render(request, "voting.html", {"nominees": nominees, "has_voted": has_voted, **_role_flags(_read_current_user(request))})


def mvp(request, tournament_id: int):
    token = request.session.get("api_token")
    roles = _role_flags(_read_current_user(request))

    if request.method == "POST":
        player_id = int(request.POST.get("player_id", "0"))
        vote_result = api_client.vote_mvp(tournament_id, player_id, token=token)
        if vote_result.ok:
            messages.success(request, "Голос за MVP принят")
        else:
            messages.error(request, (vote_result.error or {}).get("message", "Ошибка голосования"))

    mvp_result = api_client.get_mvp(tournament_id, token=token)
    payload = mvp_result.data if mvp_result.ok else {"isOpen": False, "candidates": [], "results": []}
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
                login_result = api_client.login(form.cleaned_data["email"], form.cleaned_data["password"])
                if login_result.ok:
                    token = (login_result.data or {}).get("token")
                    if token:
                        request.session["api_token"] = token
                        me_result = api_client.me(token)
                        request.session["current_user"] = me_result.data if me_result.ok else (login_result.data or {}).get("user", {})
                    messages.success(request, "Регистрация успешна. Вы вошли в систему.")
                    return redirect("tournaments")
                messages.success(request, "Регистрация успешна. Теперь войдите в систему.")
                return redirect("login")
            form.add_error(None, (register_result.error or {}).get("message", "Ошибка регистрации"))
    else:
        form = RegistrationForm()

    return render(request, "registration.html", {"form": form, **_role_flags(_read_current_user(request))})


def profile(request):
    user = _read_current_user(request)
    if not user:
        messages.info(request, "Войдите, чтобы открыть профиль.")
        return redirect("login")

    roles = _role_flags(user)
    selected_game = (request.GET.get("game") or "counterstrike").strip() or "counterstrike"
    nickname = (user.get("nickname") or "").strip()
    esports_payload = None
    esports_results = []
    esports_error = None

    if nickname:
        result = api_client.esports_player(nickname, game=selected_game)
        if result.ok:
            esports_payload = result.data or {}
            esports_results = (esports_payload or {}).get("results") or []
            if not esports_results:
                esports_error = "Игрок не найден в PandaScore."
        else:
            esports_error = (result.error or {}).get("message", "Не удалось получить данные игрока")
    else:
        esports_error = "У аккаунта не задан ник."

    return render(request, "profile.html", {"selected_game": selected_game, "esports_payload": esports_payload, "esports_results": esports_results, "esports_error": esports_error, **roles})


def streams(request):
    roles = _role_flags(_read_current_user(request))
    tournament_payload = None
    player_payload = None
    tournament_query = ""
    player_query = ""
    diagnostics = None
    host = (request.get_host() or "localhost").split(":")[0]

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
                            "channel": stream.get("channel") or (_extract_twitch_channel(url) if provider == "twitch" else ""),
                            "embed_url": _build_youtube_embed(url) if provider == "youtube" else url,
                            "matchName": stream.get("matchName") or "",
                            "viewerCount": stream.get("viewerCount"),
                            "isLive": stream.get("isLive"),
                        })
                    payload["streams_prepared"] = streams_prepared
                    tournament_payload = payload
                else:
                    messages.error(request, (result.error or {}).get("message", "Не удалось получить стримы"))
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
                    messages.error(request, (result.error or {}).get("message", "Игрок не найден"))
                    diagnostics = api_client.esports_diagnostics().data
            else:
                messages.error(request, "Введите ник игрока")

    return render(request, "streams.html", {"tournament_payload": tournament_payload, "player_payload": player_payload, "tournament_query": tournament_query, "player_query": player_query, "twitch_parent": host, "diagnostics": diagnostics, **roles})


def analytics(request):
    result = api_client.get_analytics(token=request.session.get("api_token"))
    payload = result.data if result.ok else {"playerStats": [], "disciplinePopularity": [], "prizePools": [], "summary": {}}
    if not result.ok:
        messages.info(request, (result.error or {}).get("message", "Аналитика недоступна"))
    return render(request, "analytics.html", {"analytics": payload, **_role_flags(_read_current_user(request))})
