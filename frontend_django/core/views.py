from datetime import date

from django.contrib import messages
from django.http import HttpResponseForbidden
from django.shortcuts import redirect, render

from .api_client import api_client
from .forms import LoginForm, MatchResultForm, RegistrationForm


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
    if not stats_result.ok:
        messages.info(request, "API недоступно, показаны демо-данные")
        stats = {"players": 12000, "tournaments": 3, "viewers": 860000, "events_today": 4, "today": date.today()}
    else:
        payload = stats_result.data or {}
        stats = {
            "players": payload.get("totalPlayers", 12000),
            "tournaments": payload.get("activeTournaments", 3),
            "viewers": payload.get("totalViewers", 860000),
            "events_today": payload.get("eventsToday", 4),
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
                request.session["api_token"] = login_result.data.get("token")
                request.session["current_user"] = login_result.data.get("user", {})
                messages.success(request, "Вход выполнен")
                return redirect("dashboard")
            messages.error(request, (login_result.error or {}).get("message", "Ошибка входа"))
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
    if not tournaments_data:
        tournaments_data = [_normalize_tournament({"id": 1, "name": "Демо-турнир"})]

    return render(request, "tournaments.html", {"tournaments": tournaments_data, **_role_flags(_read_current_user(request))})


def tournament_detail(request, tournament_id: int):
    token = request.session.get("api_token")
    tournament_result = api_client.get_tournament(tournament_id, token=token)
    redirect_or_none = _process_result_error(request, tournament_result)
    if redirect_or_none:
        return redirect_or_none

    if not tournament_result.ok:
        messages.error(request, "Турнир не найден")
        return redirect("tournaments")

    matches_result = api_client.get_matches(tournament_id, token=token)
    matches = [_normalize_match(item) for item in (matches_result.data or [])] if matches_result.ok else []

    return render(
        request,
        "tournament_detail.html",
        {
            "tournament": _normalize_tournament(tournament_result.data or {}),
            "matches": matches,
            **_role_flags(_read_current_user(request)),
        },
    )


def match_center(request, tournament_id: int):
    token = request.session.get("api_token")
    user = _read_current_user(request)
    roles = _role_flags(user)
    result_form = MatchResultForm(request.POST or None)

    if request.method == "POST":
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

    return render(request, "match.html", {"matches": matches, "form": result_form, "tournament_id": tournament_id, **roles})


def voting(request):
    nominees_result = api_client.get_nominees()
    nominees = nominees_result.data if nominees_result.ok and nominees_result.data else [{"id": 1, "name": "s1mple", "team": "NaVi", "role": "AWP", "kda": "1.20", "rating": "1.30", "votes": 0}]

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
        form = RegistrationForm(request.POST, request.FILES)
        if form.is_valid():
            register_result = api_client.register(
                form.cleaned_data["email"],
                form.cleaned_data["password"],
                form.cleaned_data["role"],
            )
            if register_result.ok:
                messages.success(request, "Регистрация успешна. Теперь войдите в систему.")
                return redirect("login")
            messages.error(request, (register_result.error or {}).get("message", "Ошибка регистрации"))
    else:
        form = RegistrationForm()

    return render(request, "registration.html", {"form": form, **_role_flags(_read_current_user(request))})


def streams(request):
    result = api_client.get_streams(token=request.session.get("api_token"))
    streams_data = result.data if result.ok and result.data else [{"provider": "Twitch", "url": "н/д", "status": {"online": False, "viewers": 0}}]
    return render(request, "streams.html", {"streams": streams_data, **_role_flags(_read_current_user(request))})


def analytics(request):
    result = api_client.get_analytics(token=request.session.get("api_token"))
    payload = result.data if result.ok else {"playerStats": [], "disciplinePopularity": []}
    if not result.ok:
        messages.info(request, "Аналитика недоступна")
    return render(request, "analytics.html", {"analytics": payload, **_role_flags(_read_current_user(request))})
