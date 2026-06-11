# frontend_django/esports_tournament/urls.py
from django.conf import settings
from django.conf.urls.static import static
from django.contrib import admin
from django.urls import path
from core import views

urlpatterns = [
    path('admin/', admin.site.urls),
    path('', views.dashboard, name='dashboard'),
    path('login/', views.login_view, name='login'),
    path('logout/', views.logout_view, name='logout'),
    path('profile/', views.profile, name='profile'),
    path('profile/steam/callback', views.steam_callback, name='steam_callback'),
    path('profile/faceit/callback', views.faceit_callback, name='faceit_callback'),
    # ДОБАВЛЯЕМ ЭТУ СТРОКУ:
    path('verify-email/', views.verify_email_view, name='verify_email'),
    
    # Pro Zone
    path('pro/tournaments/', views.tournaments, {'is_pro': True}, name='pro_tournaments'),
    path('pro/tournaments/<int:tournament_id>/', views.tournament_detail, name='pro_tournament_detail'),
    path('pro/tournaments/<int:tournament_id>/matches/', views.match_center, name='pro_match_center'),
    path('pro/tournaments/<int:tournament_id>/mvp/', views.mvp, name='pro_mvp'),
    path('pro/analytics/', views.analytics, name='pro_analytics'),
    path('pro/streams/', views.streams, name='pro_streams'),
    path('pro/voting/', views.voting, name='pro_voting'),
    path('pro/leaderboard/', views.leaderboard, name='pro_leaderboard'),

    # Play Zone
    path('pro/tournaments/<int:tournament_id>/fantasy/', views.fantasy_draft, name='fantasy_draft'),
    path('pro/tournaments/<int:tournament_id>/fantasy/submit/', views.fantasy_draft_submit, name='fantasy_draft_submit'),
    path('pro/tournaments/<int:tournament_id>/fantasy/leaderboard/', views.fantasy_leaderboard, name='fantasy_leaderboard'),
    path('play/tournaments/', views.tournaments, {'is_pro': False}, name='play_tournaments'),
    path('play/tournaments/<int:tournament_id>/', views.tournament_detail, name='play_tournament_detail'),
    path('play/tournaments/<int:tournament_id>/matches/', views.match_center, name='play_match_center'),
    path('play/scouting/', views.scouting, name='play_scouting'),
    path('registration/', views.registration, name='registration'),
    path('play/scouting/team/<int:team_id>/', views.smart_scouting, name='smart_scouting'),
    path('play/scouting/team/<int:team_id>/swipe/', views.smart_scouting_swipe, name='smart_scouting_swipe'),
    path('play/teams/', views.teams, name='play_teams'),
]

if settings.DEBUG:
    urlpatterns += static(settings.MEDIA_URL, document_root=settings.MEDIA_ROOT)