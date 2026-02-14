from datetime import date
from django.shortcuts import render, redirect
from django.contrib import messages
from django.conf import settings
from .forms import RegistrationForm
from .api_client import api_client

def dashboard(request):
    # Получаем статистику из C# API
    stats_data = api_client.get_stats()
    
    if stats_data:
        stats = {
            "players": stats_data.get('totalPlayers', 12000),
            "tournaments": stats_data.get('activeTournaments', 3),
            "viewers": stats_data.get('totalViewers', 860000),
            "events_today": stats_data.get('eventsToday', 4),
            "today": date.today(),
        }
    else:
        # Fallback на локальные данные если API недоступно
        stats = {
            "players": 12000,
            "tournaments": 3,
            "viewers": 860000,
            "events_today": 4,
            "today": date.today(),
        }
    
    return render(request, "dashboard.html", {"stats": stats})

def tournaments(request):
    # Получаем турниры из C# API
    tournaments_data = api_client.get_tournaments()
    
    if not tournaments_data:
        # Fallback данные
        tournaments_data = [
            {
                "id": 1,
                "name": "Чемпионат Major по CS:GO",
                "game": "CS:GO",
                "prizePool": "$1 000 000",
                "participants": "24/32",
                "startDate": "24 октября 2026",
                "status": "Регистрация",
            },
            # ... остальные турниры
        ]
    
    return render(request, "tournaments.html", {"tournaments": tournaments_data})

def tournament_detail(request, tournament_id: int):
    tournament = api_client.get_tournament(tournament_id)
    
    if not tournament:
        messages.error(request, "Турнир не найден.")
        return redirect("tournaments")
    
    return render(request, "tournament_detail.html", {
        "tournament": tournament,
        "matches": tournament.get('matches', [])
    })

def voting(request):
    # Получаем номинантов из C# API
    nominees = api_client.get_nominees()
    
    if not nominees:
        # Fallback данные
        nominees = [
            {"id": 1, "name": "s1mple", "team": "NaVi", "role": "Снайпер", "kda": "1.42", "rating": "1.35", "votes": 1240},
            # ... остальные номинанты
        ]
    
    # Проверяем, голосовал ли пользователь
    session_id = request.session.session_key
    has_voted = False
    
    if session_id:
        voted_data = api_client.has_voted(session_id)
        has_voted = voted_data.get('hasVoted', False) if voted_data else False
    
    if request.method == "POST":
        if has_voted:
            messages.info(request, "Ты уже голосовал(а) в этой сессии.")
            return redirect("voting")
        
        try:
            nominee_id = int(request.POST.get("nominee_id", "0"))
        except ValueError:
            nominee_id = 0
        
        # Голосуем через C# API
        ip_address = request.META.get('REMOTE_ADDR', '')
        vote_result = api_client.vote(nominee_id, session_id, ip_address)
        
        if vote_result and vote_result.get('success'):
            messages.success(request, vote_result.get('message', 'Голос засчитан!'))
            request.session['voted'] = True
            return redirect("voting")
        else:
            error_msg = vote_result.get('message', 'Ошибка при голосовании') if vote_result else 'Ошибка соединения'
            messages.error(request, error_msg)
    
    return render(request, "voting.html", {
        "nominees": nominees,
        "voted_id": request.session.get('voted_id'),
        "has_voted": has_voted
    })


def registration(request):
    """Страница регистрации (учебная форма).

    Сейчас данные не сохраняются в БД, а просто валидируются и показывается сообщение.
    (Можно расширить и отправлять в C# API/сохранять в Django модели.)
    """
    if request.method == "POST":
        form = RegistrationForm(request.POST, request.FILES)
        if form.is_valid():
            messages.success(request, "Заявка принята! (учебный режим)")
            return redirect("registration")
    else:
        form = RegistrationForm()

    return render(request, "registration.html", {"form": form})


def streams(request):
    """Страница стримов (пока статическая)."""
    return render(request, "streams.html")