from datetime import date

from django.contrib import messages
from django.http import HttpResponseForbidden
from django.shortcuts import redirect, render

from .api_client import api_client
from .forms import LoginForm, MatchResultForm, RegistrationForm, TeamCreateForm, TeamPlayerForm

ROLE_ADMIN = "admin"
ROLE_JUDGE = "judge"
ROLE_CAPTAIN = "captain"
STATUS_LABELS = {
    "planned": "Запланирован",
    "live": "Идёт",
    "finished": "Завершён",
    "approved": "Подтверждён",
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
        "status": status_key,
        "status_label": status_label,
        "startDate": item.get("startDate") or item.get("start_date") or "н/д",
        "prizePool": item.get("prizePool") or item.get("prize_pool") or item.get("totalAmount") or "н/д",
        "participants": item.get("participants") or f"{item.get('currentParticipants', 'N/A')}/{item.get('maxParticipants', 'N/A')}",
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
        "groupName": item.get("groupName", "н/д"),
        "streamUrl": item.get("streamUrl", "н/д"),
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
        "current_user": user,
    }


def _process_result_error(request, result):
    if result.ok:
        return None
    code = (result.error or {}).get("code")
    message = (result.error or {}).get("message", "Ошибка API")
    if code == "unauthorized":
        request.session.pop("api_token", None)
        request.session.pop("current_user", None)
        messages.info(request, "Сессия истекла. Войдите заново.")
        return redirect("login")
    if code == "forbidden":
        messages.error(request, "Недостаточно прав")
        return HttpResponseForbidden("Недостаточно прав")

    messages.error(request, message)
    return None


def dashboard(request):
    stats_result = api_client.get_stats()
    if not stats_result.ok and (stats_result.error or {}).get("code") == "api_unavailable":
        messages.info(request, "API недоступно, показаны демо-данные")
        stats = {"players": 12000, "tournaments": 3, "viewers": 860000, "events_today": 4, "today": date.today()}
    else:
        payload = stats_result.data or {}
        stats = {
            "players": payload.get("totalPlayers", 0),
            "tournaments": payload.get("activeTournaments", 0),
            "viewers": payload.get("totalViewers", 0),
            "events_today": payload.get("eventsToday", 0),
            "today": date.today(),
        }

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
                user_data = (login_result.data or {}).get("user", {})
                if not token:
                    form.add_error(None, "API вернул некорректный ответ авторизации")
                    return render(request, "login.html", {"form": form, **_role_flags(_read_current_user(request))})

                request.session["api_token"] = token
                me_result = api_client.me(token)
                if me_result.ok and me_result.data:
                    request.session["current_user"] = me_result.data
                else:
                    request.session["current_user"] = user_data
                messages.success(request, "Вход выполнен")
                return redirect("dashboard")

            error = login_result.error or {}
            code = error.get("code")
            details = error.get("details", {})

            if code == "unauthorized":
                form.add_error(None, "Неверный email или пароль")
            elif code == "validation_error" and isinstance(details, dict) and details.get("errors"):
                for field, field_errors in details["errors"].items():
                    key = field.lower()
                    form.add_error(key if key in form.fields else None, ", ".join(field_errors))
            elif code == "api_unavailable":
                form.add_error(None, "API недоступно. Попробуйте позже.")
            else:
                form.add_error(None, error.get("message", "Ошибка входа"))
    else:
        form = LoginForm()

    return render(request, "login.html", {"form": form, **_role_flags(_read_current_user(request))})


def logout_view(request):
    request.session.flush()
    messages.info(request, "Вы вышли из системы")
    return redirect("login")


def tournaments(request):
    token = request.session.get("api_token")
    result = api_client.get_tournaments(token=token)
    redirect_or_none = _process_result_error(request, result)
    if redirect_or_none:
        return redirect_or_none

    tournaments_data = [_normalize_tournament(item) for item in (result.data or [])] if result.ok else []
    if not result.ok and (result.error or {}).get("code") == "api_unavailable":
        messages.info(request, "API недоступно, показаны демо-данные")
        tournaments_data = [_normalize_tournament({"id": 1, "name": "Демо-турнир"})]

    return render(request, "tournaments.html", {"tournaments": tournaments_data, **_role_flags(_read_current_user(request))})


def teams(request):
    token = request.session.get("api_token")
    if not token:
        messages.info(request, "Для управления командами нужно войти в систему")
        return redirect("login")

    if request.method == "POST":
        action = request.POST.get("action")
        if action == "create_team":
            team_form = TeamCreateForm(request.POST)
            if team_form.is_valid():
                create_result = api_client.create_team(team_form.cleaned_data["name"], token)
                redirect_or_none = _process_result_error(request, create_result)
                if redirect_or_none:
                    return redirect_or_none
                if create_result.ok:
                    messages.success(request, "Команда создана")
                    return redirect("teams")
                messages.error(request, (create_result.error or {}).get("message", "Не удалось создать команду"))
        elif action == "add_player":
            player_form = TeamPlayerForm(request.POST)
            if player_form.is_valid():
                add_result = api_client.add_team_player(player_form.cleaned_data["team_id"], player_form.cleaned_data["nickname"], token)
                redirect_or_none = _process_result_error(request, add_result)
                if redirect_or_none:
                    return redirect_or_none
                if add_result.ok:
                    messages.success(request, "Игрок добавлен в команду")
                    return redirect("teams")
                messages.error(request, (add_result.error or {}).get("message", "Не удалось добавить игрока"))

        elif action == "delete_player":
            try:
                team_id = int(request.POST.get("team_id") or 0)
                player_id = int(request.POST.get("player_id") or 0)
            except ValueError:
                messages.error(request, "Некорректные данные удаления игрока")
                return redirect("teams")

            delete_result = api_client.delete_team_player(team_id, player_id, token)
            redirect_or_none = _process_result_error(request, delete_result)
            if redirect_or_none:
                return redirect_or_none
            if delete_result.ok:
                messages.success(request, "Игрок удалён")
            else:
                messages.error(request, (delete_result.error or {}).get("message", "Не удалось удалить игрока"))
            return redirect("teams")

        elif action == "delete_team":
            try:
                team_id = int(request.POST.get("team_id") or 0)
            except ValueError:
                messages.error(request, "Некорректные данные удаления команды")
                return redirect("teams")

            delete_result = api_client.delete_team(team_id, token)
            redirect_or_none = _process_result_error(request, delete_result)
            if redirect_or_none:
                return redirect_or_none
            if delete_result.ok:
                messages.success(request, "Команда удалена")
            else:
                messages.error(request, (delete_result.error or {}).get("message", "Не удалось удалить команду"))
            return redirect("teams")

    teams_result = api_client.get_teams(token=token)
    redirect_or_none = _process_result_error(request, teams_result)
    if redirect_or_none:
        return redirect_or_none

    teams_data = teams_result.data if teams_result.ok else []
    return render(
        request,
        "teams.html",
        {
            "teams": teams_data,
            "team_form": TeamCreateForm(),
            "player_form": TeamPlayerForm(),
            **_role_flags(_read_current_user(request)),
        },
    )


def tournament_detail(request, tournament_id: int):
    token = request.session.get("api_token")
    user = _read_current_user(request)
    roles = _role_flags(user)

    # Apply form (captain only)
    if request.method == "POST":
        action = request.POST.get("action")
        if action == "apply_to_tournament":
            if not token or not roles["is_captain"]:
                messages.error(request, "Только капитан может подавать заявки")
                return redirect("login")
            team_id_raw = request.POST.get("team_id")
            try:
                team_id = int(team_id_raw or "0")
            except ValueError:
                team_id = 0
            if team_id <= 0:
                messages.error(request, "Выберите команду")
            else:
                apply_result = api_client.apply_to_tournament(tournament_id, team_id, token=token)
                redirect_or_none = _process_result_error(request, apply_result)
                if redirect_or_none:
                    return redirect_or_none
                if apply_result.ok:
                    messages.success(request, "Заявка отправлена")
                else:
                    messages.error(request, (apply_result.error or {}).get("message", "Не удалось отправить заявку"))
            return redirect("tournament_detail", tournament_id=tournament_id)

    tournament_result = api_client.get_tournament(tournament_id, token=token)
    redirect_or_none = _process_result_error(request, tournament_result)
    if redirect_or_none:
        return redirect_or_none

    if not tournament_result.ok:
        messages.error(request, "Турнир не найден")
        return redirect("tournaments")

    tournament = _normalize_tournament(tournament_result.data or {})

    matches_result = api_client.get_matches(tournament_id, token=token)
    matches = [_normalize_match(item) for item in (matches_result.data or [])] if matches_result.ok else []

    # For captain: list captain teams & own applications
    my_teams = []
    my_apps = []
    if roles["is_captain"] and token and user:
        teams_result = api_client.get_teams(token=token)
        if teams_result.ok:
            email = (user.get("email") or "").lower()
            for t in teams_result.data or []:
                if (t.get("captainEmail") or "").lower() == email:
                    my_teams.append(t)

        apps_result = api_client.my_tournament_applications(tournament_id, token=token)
        if apps_result.ok:
            my_apps = apps_result.data or []

    return render(
        request,
        "tournament_detail.html",
        {
            "tournament": tournament,
            "matches": matches,
            "my_teams": my_teams,
            "my_apps": my_apps,
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
            messages.info(request, "Матчи из PandaScore доступны только для просмотра (read-only).")
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
            redirect_or_none = _process_result_error(request, update_result)
            if redirect_or_none:
                return redirect_or_none
            if update_result.ok:
                messages.success(request, "Результат обновлён")
            else:
                messages.error(request, (update_result.error or {}).get("message", "Ошибка обновления"))

    matches_result = api_client.get_matches(tournament_id, token=token)
    matches = [_normalize_match(item) for item in (matches_result.data or [])] if matches_result.ok else []
    if not matches:
        messages.info(request, "Матчи недоступны, показан пустой список")

    return render(
        request,
        "match.html",
        {
            "matches": matches,
            "form": result_form,
            "tournament_id": tournament_id,
            "matches_readonly": is_external,
            **roles,
        },
    )




def voting(request):
    nominees_result = api_client.get_nominees()
    nominees = nominees_result.data if nominees_result.ok and nominees_result.data else []
    if not nominees_result.ok and (nominees_result.error or {}).get("code") == "api_unavailable":
        messages.info(request, "API недоступно, список номинантов временно пуст")

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
        redirect_or_none = _process_result_error(request, vote_result)
        if redirect_or_none:
            return redirect_or_none
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
                messages.success(request, "Регистрация успешна. Теперь войдите в систему.")
                return redirect("login")

            error = register_result.error or {}
            details = error.get("details", {})
            code = error.get("code")

            if isinstance(details, dict) and details.get("errors"):
                for field, field_errors in details["errors"].items():
                    key = field.lower()
                    form.add_error(key if key in form.fields else None, ", ".join(field_errors))
            elif code == "conflict":
                form.add_error("email", error.get("message", "Пользователь с таким email уже существует"))
            elif code == "api_unavailable":
                form.add_error(None, "API недоступно. Попробуйте позже.")
            else:
                form.add_error(None, error.get("message", "Ошибка регистрации"))
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
    esports_fields = []
    esports_error = None

    if nickname:
        result = api_client.esports_player(nickname, game=selected_game)
        if result.ok:
            esports_payload = result.data or {}
            info = (esports_payload or {}).get("info") or {}
            labels = {
                "id": "ID",
                "name": "Имя",
                "fullname": "Полное имя",
                "romanized_name": "Имя (romanized)",
                "country": "Страна",
                "nationality": "Гражданство",
                "team": "Команда",
                "team1": "Команда",
                "role": "Роль",
                "roles": "Роли",
                "status": "Статус",
                "years_active": "Годы активности",
                "approx_earnings": "Примерный заработок",
            }
            order = ["id", "name", "fullname", "romanized_name", "country", "nationality", "team", "role", "roles", "status", "years_active", "approx_earnings"]
            used = set()
            for k in order:
                v = info.get(k)
                if v:
                    esports_fields.append({"key": labels.get(k, k), "value": v})
                    used.add(k)
            # остальное
            for k, v in info.items():
                if k in used:
                    continue
                if v:
                    esports_fields.append({"key": labels.get(k, k), "value": v})
        else:
            esports_error = (result.error or {}).get("message", "Не удалось получить данные игрока")
    else:
        esports_error = "У аккаунта не задан ник."

    return render(
        request,
        "profile.html",
        {
            "selected_game": selected_game,
            "esports_payload": esports_payload,
            "esports_fields": esports_fields,
            "esports_error": esports_error,
            **roles,
        },
    )


def streams(request):
    roles = _role_flags(_read_current_user(request))

    tournament_payload = None
    player_payload = None
    tournament_query = ""
    player_query = ""

    # Twitch embed требует parent без порта (пример: localhost)
    host = (request.get_host() or "localhost").split(":")[0]

    if request.method == "POST":
        action = request.POST.get("action")

        if action == "find_tournament_streams":
            tournament_query = (request.POST.get("tournament_query") or "").strip()
            if tournament_query:
                result = api_client.esports_tournament_streams(tournament_query)
                if result.ok:
                    payload = result.data or {}
                    # Prepare stream cards for template
                    streams = []
                    for s in payload.get("streams", []) or []:
                        url = s.get("url") or s.get("rawUrl") or ""
                        if not url:
                            continue
                        provider = _detect_stream_provider(url)
                        streams.append(
                            {
                                "provider": provider,
                                "url": url,
                                "channel": _extract_twitch_channel(url) if provider == "twitch" else "",
                                "matchName": s.get("matchName") or s.get("matchName") or "",
                            }
                        )
                    payload["streams_prepared"] = streams
                    tournament_payload = payload
                else:
                    messages.error(request, (result.error or {}).get("message", "Не удалось получить стримы"))
            else:
                messages.error(request, "Введите название турнира или запрос")

        elif action == "find_player":
            player_query = (request.POST.get("player_query") or "").strip()
            if player_query:
                result = api_client.esports_player(player_query)
                if result.ok:
                    player_payload = result.data
                else:
                    messages.error(request, (result.error or {}).get("message", "Игрок не найден"))
            else:
                messages.error(request, "Введите ник игрока")

    context = {
        "tournament_payload": tournament_payload,
        "player_payload": player_payload,
        "tournament_query": tournament_query,
        "player_query": player_query,
        "twitch_parent": host,
    }
    context.update(roles)
    return render(request, "streams.html", context)



def analytics(request):
    result = api_client.get_analytics(token=request.session.get("api_token"))
    payload = result.data if result.ok else {"playerStats": [], "disciplinePopularity": []}
    if not result.ok:
        messages.info(request, "Аналитика недоступна")
    return render(request, "analytics.html", {"analytics": payload, **_role_flags(_read_current_user(request))})
