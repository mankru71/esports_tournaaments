import base64
import json
import logging
import time
from dataclasses import dataclass
from typing import Any

import requests
from django.conf import settings

logger = logging.getLogger(__name__)


@dataclass
class ApiResult:
    ok: bool
    data: Any = None
    error: dict | None = None


class CSharpApiClient:
    def __init__(self):
        self.base_url = settings.DJANGO_API_BASE_URL.rstrip("/")
        self.timeout = settings.C_SHARP_API.get("TIMEOUT", 30)
        self.session = requests.Session()

    def _build_url(self, endpoint: str) -> str:
        return f"{self.base_url}/{endpoint.lstrip('/')}"

    def _decode_exp(self, token: str) -> int | None:
        try:
            payload = token.split(".")[1]
            padding = "=" * (-len(payload) % 4)
            decoded = base64.urlsafe_b64decode(payload + padding)
            return int(json.loads(decoded.decode("utf-8")).get("exp"))
        except Exception:
            return None

    def _request(self, method: str, endpoint: str, data=None, params=None, token: str | None = None) -> ApiResult:
        url = self._build_url(endpoint)
        headers = {"Content-Type": "application/json"}
        if token:
            headers["Authorization"] = f"Bearer {token}"

        try:
            response = self.session.request(
                method=method,
                url=url,
                json=data,
                params=params,
                timeout=self.timeout,
                headers=headers,
            )
        except requests.exceptions.RequestException as exc:
            logger.error("C# API connection error for %s %s: %s", method.upper(), url, exc)
            return ApiResult(ok=False, error={"code": "api_unavailable", "message": "API недоступно", "details": str(exc)})

        if 200 <= response.status_code < 300:
            if not response.content:
                return ApiResult(ok=True, data=None)
            try:
                return ApiResult(ok=True, data=response.json())
            except ValueError:
                return ApiResult(ok=True, data={"raw": response.text})

        body = {}
        try:
            body = response.json()
        except ValueError:
            body = {"message": response.text} if response.text else {}

        message = (body or {}).get("message") or (body or {}).get("detail") or (body or {}).get("title") or "Ошибка API"

        if response.status_code == 401:
            return ApiResult(ok=False, error={"code": "unauthorized", "message": message or "Требуется вход", "details": body})
        if response.status_code == 403:
            return ApiResult(ok=False, error={"code": "forbidden", "message": message or "Недостаточно прав", "details": body})
        if response.status_code == 400:
            return ApiResult(ok=False, error={"code": "validation_error", "message": message or "Проверьте корректность данных", "details": body})
        if response.status_code == 404:
            return ApiResult(ok=False, error={"code": "not_found", "message": message or "Ресурс не найден", "details": body})
        if response.status_code == 409:
            return ApiResult(ok=False, error={"code": "conflict", "message": message or "Конфликт данных", "details": body})
        if response.status_code >= 500:
            return ApiResult(ok=False, error={"code": "server_error", "message": message or "Ошибка сервера API", "details": body})

        return ApiResult(ok=False, error={"code": f"http_{response.status_code}", "message": message, "details": body})

    def token_expired(self, token: str | None) -> bool:
        if not token:
            return True
        exp = self._decode_exp(token)
        if not exp:
            return False
        return exp <= int(time.time())

    def login(self, email: str, password: str) -> ApiResult:
        return self._request("POST", "auth/login", data={"email": email, "password": password})

    def register(self, email: str, password: str, nickname: str, role: str = "captain") -> ApiResult:
        return self._request("POST", "auth/register", data={"email": email, "password": password, "nickname": nickname, "role": role})

    def me(self, token: str) -> ApiResult:
        return self._request("GET", "auth/me", token=token)

    def get_tournaments(self, token: str | None = None, search: str | None = None) -> ApiResult:
        params = {}
        if search:
            params["search"] = search
        return self._request("GET", "tournament", params=params, token=token)

    def update_profile(self, nickname: str, bio: str, token: str, game_role: str = "", availability: str = "", pitch: str = "", discord_id: str = "", country: str = "", city: str = "", languages: str = "", highlights_url: str = "") -> ApiResult:
        data = {
            "nickname": nickname, 
            "bio": bio,
            "gameRole": game_role,
            "availability": availability,
            "pitch": pitch,
            "discordId": discord_id,
            "highlightsUrl": highlights_url,
            "country": country,
            "city": city,
            "languages": languages
        }
        return self._request("PUT", "auth/profile", data=data, token=token)

    def verify_rating(self, provider: str, profile_url: str, token: str) -> ApiResult:
        return self._request("POST", "auth/profile/verify-rating", data={"provider": provider, "profileUrl": profile_url}, token=token)

    def set_looking_for_team(self, enabled: bool, token: str) -> ApiResult:
        return self._request("POST", "auth/profile/looking-for-team", data={"enabled": enabled}, token=token)

    def get_rating_history(self, token: str) -> ApiResult:
        return self._request("GET", "auth/profile/rating-history", token=token)

    # ── Scouting & Invites ──

    def get_free_agents(self) -> ApiResult:
        return self._request("GET", "scouting/free-agents")

    def get_vacancies(self) -> ApiResult:
        return self._request("GET", "scouting/vacancies")

    def create_vacancy(self, team_id: int, required_role: str, description: str, token: str) -> ApiResult:
        return self._request("POST", f"teams/{team_id}/vacancies", data={"requiredRole": required_role, "description": description}, token=token)

    def delete_vacancy(self, team_id: int, vacancy_id: int, token: str) -> ApiResult:
        return self._request("DELETE", f"teams/{team_id}/vacancies/{vacancy_id}", token=token)

    def send_invite(self, team_id: int, user_id: int, token: str) -> ApiResult:
        return self._request("POST", f"teams/{team_id}/invites", data={"userId": user_id}, token=token)

    def respond_invite(self, invite_id: int, action: str, token: str) -> ApiResult:
        return self._request("POST", f"teams/invites/{invite_id}/respond", data={"action": action}, token=token)

    def create_tournament(self, token: str, payload: dict) -> ApiResult:
        return self._request("POST", "tournament", data=payload, token=token)

    def delete_tournament(self, tournament_id: int, token: str) -> ApiResult:
        return self._request("DELETE", f"tournament/{tournament_id}", token=token)
    
    def get_tournament(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", f"tournament/{tournament_id}", token=token)

    def get_stats(self) -> ApiResult:
        return self._request("GET", "tournament/stats")

    def get_teams(self, token: str | None = None) -> ApiResult:
        return self._request("GET", "teams", token=token)

    def create_team(self, name: str, token: str) -> ApiResult:
        return self._request("POST", "teams", data={"name": name}, token=token)

    def add_team_player(self, team_id: int, nickname: str, token: str, rating: str | None = None, game: str | None = None) -> ApiResult:
        payload = {"nickname": nickname}
        if rating not in (None, ""):
            payload["rating"] = rating
        if game:
            payload["game"] = game
        return self._request("POST", f"teams/{team_id}/players", data=payload, token=token)

    def confirm_team_player_rating(self, team_id: int, player_id: int, token: str) -> ApiResult:
        return self._request("POST", f"teams/{team_id}/players/{player_id}/confirm-rating", token=token)

    def generate_tournament_bracket(self, tournament_id: int, token: str) -> ApiResult:
        return self._request("POST", f"tournament/{tournament_id}/generate-bracket", token=token)
    
    def delete_team_player(self, team_id: int, player_id: int, token: str) -> ApiResult:
        return self._request("DELETE", f"teams/{team_id}/players/{player_id}", token=token)

    def delete_team(self, team_id: int, token: str) -> ApiResult:
        return self._request("DELETE", f"teams/{team_id}", token=token)

    def apply_to_tournament(self, tournament_id: int, team_id: int, token: str) -> ApiResult:
        return self._request("POST", f"tournament/{tournament_id}/applications", data={"teamId": team_id}, token=token)

    def my_tournament_applications(self, tournament_id: int, token: str) -> ApiResult:
        return self._request("GET", f"tournament/{tournament_id}/applications/my", token=token)

    def list_tournament_applications(self, tournament_id: int, token: str) -> ApiResult:
        return self._request("GET", f"tournament/{tournament_id}/applications", token=token)

    def approve_tournament_application(self, tournament_id: int, application_id: int, token: str) -> ApiResult:
        return self._request("POST", f"tournament/{tournament_id}/applications/{application_id}/approve", token=token)

    def reject_tournament_application(self, tournament_id: int, application_id: int, token: str) -> ApiResult:
        return self._request("POST", f"tournament/{tournament_id}/applications/{application_id}/reject", token=token)

    def get_matches(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", "matches", params={"tournamentId": tournament_id}, token=token)

    def predict_match(self, match_id: int, predicted_team_id: int, token: str) -> ApiResult:
        return self._request("POST", f"matches/{match_id}/predict", data={"predictedTeamId": predicted_team_id}, token=token)

    def update_match_result(self, match_id: str | int, score_a: int, score_b: int, token: str) -> ApiResult:
        return self._request("PUT", f"matches/{match_id}/result", data={"scoreA": score_a, "scoreB": score_b}, token=token)

    def get_mvp(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", "mvp/results", params={"tournamentId": tournament_id}, token=token)

    def vote_mvp(self, tournament_id: int, player_id: int, token: str) -> ApiResult:
        return self._request("POST", "mvp/vote", data={"tournamentId": tournament_id, "playerId": player_id}, token=token)

    def get_streams(self, token: str | None = None) -> ApiResult:
        return self._request("GET", "streams/status", token=token)

    def get_analytics(self, game: str | None = None, token: str | None = None) -> ApiResult:
        params = {}
        if game:
            params["game"] = game
        return self._request("GET", "analytics", params=params, token=token)

    def get_tournament_analytics(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", f"tournament/{tournament_id}/analytics", token=token)

    def get_team_winrates(self, game: str | None = None, token: str | None = None) -> ApiResult:
        params = {}
        if game:
            params["game"] = game
        return self._request("GET", "analytics/team-winrates", params=params, token=token)

    def get_rating_history(self, token: str) -> ApiResult:
        return self._request("GET", "auth/profile/rating-history", token=token)

    def get_free_agents(self, token: str | None = None) -> ApiResult:
        return self._request("GET", "scouting/free-agents", token=token)

    def get_free_agent(self, agent_id: int) -> ApiResult:
        return self._request("GET", f"scouting/free-agents/{agent_id}")

    def set_looking_for_team(self, enabled: bool, token: str) -> ApiResult:
        return self._request("POST", "auth/profile/looking-for-team", data={"enabled": enabled}, token=token)

    def get_live_matches(self) -> ApiResult:
        return self._request("GET", "matches/live")

    def get_hall_of_fame(self) -> ApiResult:
        return self._request("GET", "analytics/hall-of-fame")

    def get_activity(self, limit: int = 10) -> ApiResult:
        return self._request("GET", "activity", params={"limit": limit})
    
    def send_email_verification(self, user_id: int, token: str) -> ApiResult:
        return self._request("POST", f"verification/send/{user_id}", token=token)

    def confirm_email(self, user_id: int, verify_token: str) -> ApiResult:
        return self._request("POST", f"verification/confirm/{user_id}", data={"token": verify_token})

    def confirm_email_by_token(self, verify_token: str) -> ApiResult:
        return self._request("POST", "verification/confirm", data={"token": verify_token})
    
    def get_analytics_csv(self, token: str | None = None) -> ApiResult:
        return self._request("GET", "analytics/export/csv", token=token)

    def get_tournament_bracket(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", f"tournament/{tournament_id}/bracket", token=token)

    def save_tournament_planning(self, tournament_id: int, token: str, format_value: str, stage_type: str) -> ApiResult:
        return self._request("POST", f"tournament/{tournament_id}/planning", data={"format": format_value, "stageType": stage_type}, token=token)

    def get_prize_pool(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", f"tournament/{tournament_id}/prize-pool", token=token)

    def set_prize_payouts(self, tournament_id: int, token: str, payouts: list[dict]) -> ApiResult:
        return self._request("POST", f"tournament/{tournament_id}/prize-pool/payouts", data={"payouts": payouts}, token=token)

    def update_tournament_status(self, tournament_id: int, status: str, token: str) -> ApiResult:
        return self._request("POST", f"tournament/{tournament_id}/status", data={"status": status}, token=token)

    def attach_match_stream(self, match_id: int, stream_url: str, token: str) -> ApiResult:
        return self._request("PUT", f"matches/{match_id}/stream", data={"streamUrl": stream_url}, token=token)

    def distribute_prizes(self, tournament_id: int, token: str) -> ApiResult:
        return self._request("POST", f"prizes/{tournament_id}/distribute", token=token)

    def get_nominees(self) -> ApiResult:
        return self._request("GET", "voting/nominees")

    def vote(self, nominee_id: int, session_id: str | None, ip_address: str) -> ApiResult:
        return self._request("POST", "voting/vote", data={"nomineeId": nominee_id, "voterSession": session_id or "", "voterIp": ip_address})

    def has_voted(self, session_id: str) -> ApiResult:
        return self._request("GET", f"voting/hasvoted/{session_id}")

    def health_check(self) -> bool:
        return self._request("GET", "health").ok

    def get_faceit_oauth_url(self, redirect_uri: str) -> ApiResult:
        return self._request("GET", "integrations/faceit/oauth/url", params={"redirectUri": redirect_uri})

    def verify_faceit_oauth(self, user_id: int, code: str, redirect_uri: str, token: str) -> ApiResult:
        payload = {"code": code, "redirectUri": redirect_uri}
        return self._request("POST", f"integrations/faceit/oauth/verify/{user_id}", data=payload, token=token)

    def get_steam_openid_url(self, redirect_uri: str) -> ApiResult:
        return self._request("GET", "integrations/steam/openid/url", params={"redirectUri": redirect_uri})

    def verify_steam_openid(self, user_id: int, openid_params: dict, token: str) -> ApiResult:
        return self._request("POST", f"integrations/steam/openid/verify/{user_id}", data=openid_params, token=token)

    def unlink_faceit(self, user_id: int, token: str) -> ApiResult:
        return self._request("DELETE", f"integrations/faceit/unlink/{user_id}", token=token)

    def esports_player(self, nickname: str, game: str = "counterstrike") -> ApiResult:
        return self._request("GET", "esports/player", params={"nickname": nickname, "game": game})

    def esports_tournament_streams(self, query: str, game: str = "counterstrike") -> ApiResult:
        return self._request("GET", "esports/tournament/streams", params={"query": query, "game": game})

    def esports_diagnostics(self) -> ApiResult:
        return self._request("GET", "esports/diagnostics")

    def get_favorites(self, token: str) -> ApiResult:
        return self._request("GET", "favorites", token=token)

    def add_favorite(self, tournament_id: int, token: str) -> ApiResult:
        return self._request("POST", "favorites", data={"tournamentId": tournament_id}, token=token)

    def remove_favorite(self, tournament_id: int, token: str) -> ApiResult:
        return self._request("DELETE", f"favorites/{tournament_id}", token=token)

    def get_smart_scouting_recommendations(self, team_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", "scouting/recommendations", params={"teamId": team_id}, token=token)

    def swipe_smart_scouting(self, team_id: int, player_id: int, action: str, token: str | None = None) -> ApiResult:
        payload = {
            "TeamId": team_id,
            "PlayerId": player_id,
            "Action": action
        }
        return self._request("POST", "scouting/swipe", data=payload, token=token)

    def get_fantasy_players(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", f"fantasy/{tournament_id}/players", token=token)

    def submit_fantasy_draft(self, tournament_id: int, team_name: str, player_ids: list[int], token: str | None = None) -> ApiResult:
        payload = {
            "TournamentId": tournament_id,
            "TeamName": team_name,
            "PlayerIds": player_ids
        }
        return self._request("POST", "fantasy/draft", data=payload, token=token)

    def get_fantasy_leaderboard(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", f"fantasy/{tournament_id}/leaderboard", token=token)
        
api_client = CSharpApiClient()
