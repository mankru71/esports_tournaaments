from datetime import date
from django.contrib import messages
from django.http import HttpResponseForbidden
from django.shortcuts import redirect, render
from .api_client import api_client
from .forms import (
    FaceitVerifyForm,
    LoginForm,
    MatchResultForm,
    ProfileEditForm,
    RegistrationForm,
    TeamCreateForm,
    TeamPlayerForm,
    TournamentCreateForm,
)

STATUS_LABELS = {
    "planned": "Запланирован",
    "live": "Идёт",
    "paused": "Пауза",
    "finished": "Завершён",
    "approved": "Подтверждён",
    "pending": "На рассмотрении",
    "rejected": "Отклонена",
}


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
        "teamA": item.get("teamA", "н/д"),
        "teamB": item.get("teamB", "н/д"),
        "scoreA": item.get("scoreA", 0),
        "scoreB": item.get("scoreB", 0),
        "status": status_key,
        "status_label": status_label,
        "round": item.get("round", "н/д"),
        "groupName": item.get("groupName", ""),
        "streamUrl": item.get("streamUrl", "") or "",
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
    role = ((user or {}).get("role") or "").lower()
    is_admin = role == "admin"
    is_judge = role == "judge"
    is_captain = role == "captain"
    return {
        "is_admin": is_admin,
        "is_judge": is_judge,
        "is_captain": is_captain,
        "is_player": role == "player",
        "is_viewer": role in ("", "viewer"),
        "is_organizer": is_admin,
        "current_user": user,
    }


def _add_api_error(request, result, fallback="Ошибка выполнения операции"):
    messages.error(request, (result.error or {}).get("message", fallback))


def _require_auth(request, token):
    if token:
        return None
    messages.info(request, "Войдите в аккаунт")
    return redirect("login")


def verify_email_view(request):
    user_id = request.GET.get("userId")
    verify_token = request.GET.get("token")

    if not user_id or not verify_token:
        messages.error(request, "Ссылка подтверждения некорректна")
        return redirect("login")

    result = api_client.confirm_email(user_id, verify_token)
    if result.ok:
        messages.success(request, "Почта подтверждена")
    else:
        _add_api_error(request, result, "Ссылка недействительна или устарела")

    return redirect("profile" if request.session.get("api_token") else "login")


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
    return render(request, "dashboard.html", {"stats": stats, **_role_flags(_read_current_user(request))})


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
                form.add_error(None, "Сервер не вернул токен авторизации")
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
    user = _read_current_user(request)
    roles = _role_flags(user)

    if request.method == "POST":
        action = request.POST.get("action")
        auth_redirect = _require_auth(request, token)
        if auth_redirect:
            return auth_redirect

        if action == "create_tournament":
            if not roles["is_admin"]:
                messages.error(request, "Создавать турниры может только администратор")
                return redirect("tournaments")
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
                    return redirect("tournament_detail", tournament_id=new_id) if new_id else redirect("tournaments")
                _add_api_error(request, create_result, "Не удалось создать турнир")
            else:
                for errors in form.errors.values():
                    for error in errors:
                        messages.error(request, error)
            return redirect("tournaments")

        if action == "delete_tournament":
            if not roles["is_admin"]:
                messages.error(request, "Удалять турниры может только администратор")
                return redirect("tournaments")
            tournament_id = int(request.POST.get("tournament_id", "0") or 0)
            result = api_client.delete_tournament(tournament_id, token=token)
            if result.ok:
                messages.success(request, "Турнир удалён")
            else:
                _add_api_error(request, result, "Не удалось удалить турнир")
            return redirect("tournaments")

    tournaments_result = api_client.get_tournaments(token=token)
    tournaments_list = [_normalize_tournament(item) for item in (tournaments_result.data or [])]
    if not tournaments_result.ok:
        _add_api_error(request, tournaments_result, "Не удалось получить турниры")

    return render(
        request,
        "tournaments.html",
        {"tournaments": tournaments_list, "create_tournament_form": TournamentCreateForm(), **roles},
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
            return redirect("teams")

        if action == "generate_bracket":
            tournament_id = int(request.POST.get("tournament_id", "0"))
            if tournament_id > 0:
                result = api_client.generate_bracket(tournament_id, token=token)
                if result.ok:
                    messages.success(request, "Сетка турнира успешно сгенерирована!")
                else:
                    _add_api_error(request, result, "Не удалось сгенерировать сетку")
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
                _add_api_error(request, result, "Не удалось добавить игрока")
            return redirect("teams")

        if action == "confirm_rating":
            team_id = int(request.POST.get("team_id", "0") or 0)
            player_id = int(request.POST.get("player_id", "0") or 0)
            result = api_client.confirm_team_player_rating(team_id, player_id, token=token)
            if result.ok:
                messages.success(request, "Рейтинг подтверждён")
            else:
                _add_api_error(request, result, "Не удалось подтвердить рейтинг")
            return redirect("teams")

        if action == "delete_player":
            team_id = int(request.POST.get("team_id", "0") or 0)
            player_id = int(request.POST.get("player_id", "0") or 0)
            result = api_client.delete_team_player(team_id, player_id, token=token)
            if result.ok:
                messages.success(request, "Игрок удалён")
            else:
                _add_api_error(request, result, "Не удалось удалить игрока")
            return redirect("teams")

        if action == "delete_team":
            team_id = int(request.POST.get("team_id", "0") or 0)
            result = api_client.delete_team(team_id, token=token)
            if result.ok:
                messages.success(request, "Команда удалена")
            else:
                _add_api_error(request, result, "Не удалось удалить команду")
            return redirect("teams")

    teams_result = api_client.get_teams(token=token)
    teams_data = teams_result.data if teams_result.ok else []
    if not teams_result.ok:
        _add_api_error(request, teams_result, "Не удалось загрузить команды")

    return render(
        request,
        "teams.html",
        {"teams": teams_data, "team_form": TeamCreateForm(), "player_form": TeamPlayerForm(), **roles},
    )


def tournament_detail(request, tournament_id: int):
    token = request.session.get("api_token")
    user = _read_current_user(request)
    roles = _role_flags(user)
=    matches_resp = api_client.get_tournament_matches(tournament_id)
    flat_matches = matches_resp.data if matches_resp.ok else []

=    rounds_dict = {}
    for m in flat_matches:
        r = m.get('round', 1)
        if r not in rounds_dict:
            rounds_dict[r] = []
        rounds_dict[r].append(m)

=    bracket_rounds = [rounds_dict[k] for k in sorted(rounds_dict.keys())]

    # Добавляем bracket_rounds в context и возвращаем render()
    if request.method == "POST":
        action = request.POST.get("action")

        if action == "apply_to_tournament":
            auth_redirect = _require_auth(request, token)
            if auth_redirect:
                return auth_redirect
            if not roles["is_captain"]:
                messages.error(request, "Подать заявку может только капитан команды")
                return redirect("tournament_detail", tournament_id=tournament_id)
            team_id = int(request.POST.get("team_id", "0") or 0)
            result = api_client.apply_to_tournament(tournament_id, team_id, token=token)
            if result.ok:
                messages.success(request, "Заявка отправлена")
            else:
                _add_api_error(request, result, "Не удалось отправить заявку")
            return redirect("tournament_detail", tournament_id=tournament_id)

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
            return redirect("tournament_detail", tournament_id=tournament_id)

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
            return redirect("tournament_detail", tournament_id=tournament_id)

        if action == "generate_bracket":
            result = api_client.generate_tournament_bracket(tournament_id, token=token)
            if result.ok:
                messages.success(request, "Сетка сгенерирована")
            else:
                _add_api_error(request, result, "Не удалось сгенерировать сетку")
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
                        messages.error(request, f"Некорректный процент: {place}")
                        return redirect("tournament_detail", tournament_id=tournament_id)
            result = api_client.set_prize_payouts(tournament_id, token=token, payouts=payouts)
            if result.ok:
                messages.success(request, "Распределение обновлено")
            else:
                _add_api_error(request, result, "Не удалось сохранить распределение")
            return redirect("tournament_detail", tournament_id=tournament_id)

        if action == "set_status":
            status = request.POST.get("status", "planned")
            result = api_client.update_tournament_status(tournament_id, status=status, token=token)
            if result.ok:
                messages.success(request, "Статус турнира обновлён")
            else:
                _add_api_error(request, result, "Не удалось изменить статус")
            return redirect("tournament_detail", tournament_id=tournament_id)

        if action == "attach_stream":
            match_id = int(request.POST.get("match_id", "0") or 0)
            stream_url = (request.POST.get("stream_url") or "").strip()
            result = api_client.attach_match_stream(match_id, stream_url=stream_url, token=token)
            if result.ok:
                messages.success(request, "Стрим привязан к матчу")
            else:
                _add_api_error(request, result, "Не удалось привязать стрим")
            return redirect(f"/tournaments/{tournament_id}/?tab=streams")

        if action == "distribute_prizes":
            result = api_client.distribute_prizes(tournament_id, token=token)
            if result.ok:
                messages.success(request, "Призовой фонд распределён")
            else:
                _add_api_error(request, result, "Не удалось распределить призовые")
            return redirect(f"/tournaments/{tournament_id}/?tab=prize")

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
            return redirect(f"/tournaments/{tournament_id}/?tab=mvp")

    tournament_result = api_client.get_tournament(tournament_id, token=token)
    if not tournament_result.ok:
        _add_api_error(request, tournament_result, "Турнир не найден")
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

    mvp_result = api_client.get_mvp(tournament_id, token=token)
    mvp_payload = _normalize_mvp_payload(mvp_result.data if mvp_result.ok else {"isOpen": False, "candidates": [], "results": []})

    analytics_result = api_client.get_analytics(token=token)
    analytics_payload = analytics_result.data if analytics_result.ok else {"summary": {}, "playerStats": [], "disciplinePopularity": [], "prizePools": []}

    streams_result = api_client.get_streams(token=token)
    streams_payload = streams_result.data if streams_result.ok else []
    active_tab = (request.GET.get("tab") or "overview").strip() or "overview"

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
                _add_api_error(request, update_result, "Ошибка обновления")
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
                login_result = api_client.login(form.cleaned_data["email"], form.cleaned_data["password"])
                if login_result.ok:
                    token = (login_result.data or {}).get("token")
                    if token:
                        request.session["api_token"] = token
                        me_result = api_client.me(token)
                        request.session["current_user"] = me_result.data if me_result.ok else (login_result.data or {}).get("user", {})
                    messages.success(request, "Регистрация завершена")
                    return redirect("tournaments")
                messages.success(request, "Регистрация завершена. Теперь войдите в систему")
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
    edit_form = ProfileEditForm(initial={"nickname": user.get("nickname", ""), "bio": user.get("bio", "")})
    faceit_form = FaceitVerifyForm()

    if request.method == "POST":
        action = request.POST.get("action")

        if action == "save_profile":
            edit_form = ProfileEditForm(request.POST)
            if edit_form.is_valid():
                result = api_client.update_profile(edit_form.cleaned_data["nickname"], edit_form.cleaned_data.get("bio") or "", token=token)
                if result.ok:
                    request.session["current_user"] = result.data
                    messages.success(request, "Профиль обновлён")
                    return redirect("profile")
                _add_api_error(request, result, "Не удалось обновить профиль")

        elif action == "verify_faceit":
            faceit_form = FaceitVerifyForm(request.POST)
            if faceit_form.is_valid():
                nickname = faceit_form.cleaned_data["faceit_nickname"]
                result = api_client.verify_faceit_account(user_id, nickname, token=token)
                if result.ok:
                    me_res = api_client.me(token)
                    if me_res.ok:
                        request.session["current_user"] = me_res.data
                    elo = (result.data or {}).get("elo")
                    messages.success(request, f"Faceit привязан: {elo} ELO")
                    return redirect("profile")
                _add_api_error(request, result, "Ошибка Faceit")

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

    context = {
        "selected_game": selected_game,
        "esports_payload": esports_payload,
        "esports_results": esports_results,
        "esports_error": esports_error,
        "edit_form": edit_form,
        "faceit_form": faceit_form,
        **roles,
    }
    return render(request, "profile.html", context)


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
    result = api_client.get_analytics(token=request.session.get("api_token"))
    payload = result.data if result.ok else {"playerStats": [], "disciplinePopularity": [], "prizePools": [], "summary": {}}
    if not result.ok:
        messages.info(request, (result.error or {}).get("message", "Аналитика временно недоступна"))
    return render(request, "analytics.html", {"analytics": payload, **_role_flags(_read_current_user(request))})
