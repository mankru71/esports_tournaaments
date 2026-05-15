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
    # ДОБАВЛЯЕМ ЭТУ СТРОКУ:
    path('verify-email/', views.verify_email_view, name='verify_email'),
    
    path('tournaments/', views.tournaments, name='tournaments'),
    path('tournaments/<int:tournament_id>/', views.tournament_detail, name='tournament_detail'),
    path('tournaments/<int:tournament_id>/matches/', views.match_center, name='match_center'),
    path('tournaments/<int:tournament_id>/mvp/', views.mvp, name='mvp'),
    path('analytics/', views.analytics, name='analytics'),
    path('registration/', views.registration, name='registration'),
    path('teams/', views.teams, name='teams'),
    path('streams/', views.streams, name='streams'),
    path('voting/', views.voting, name='voting'),
]

if settings.DEBUG:
    urlpatterns += static(settings.MEDIA_URL, document_root=settings.MEDIA_ROOT)