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

    def get_tournaments(self, token: str | None = None) -> ApiResult:
        return self._request("GET", "tournament", token=token)

    def update_profile(self, nickname: str, bio: str, token: str) -> ApiResult:
        return self._request("PUT", "auth/profile", data={"nickname": nickname, "bio": bio}, token=token)

    def verify_rating(self, provider: str, profile_url: str, token: str) -> ApiResult:
        return self._request("POST", "auth/profile/verify-rating", data={"provider": provider, "profileUrl": profile_url}, token=token)

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

    def update_match_result(self, match_id: str | int, score_a: int, score_b: int, token: str) -> ApiResult:
        return self._request("PUT", f"matches/{match_id}/result", data={"scoreA": score_a, "scoreB": score_b}, token=token)

    def get_mvp(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", "mvp/results", params={"tournamentId": tournament_id}, token=token)

    def vote_mvp(self, tournament_id: int, player_id: int, token: str) -> ApiResult:
        return self._request("POST", "mvp/vote", data={"tournamentId": tournament_id, "playerId": player_id}, token=token)

    def get_streams(self, token: str | None = None) -> ApiResult:
        return self._request("GET", "streams/status", token=token)

    def get_analytics(self, token: str | None = None) -> ApiResult:
        return self._request("GET", "analytics", token=token)
    
    def send_email_verification(self, user_id: int, token: str) -> ApiResult:
        return self._request("POST", f"verification/send/{user_id}", token=token)

    def confirm_email(self, user_id: int | str, verify_token: str) -> ApiResult:
        payload = {"token": verify_token}
        return self._request("POST", f"verification/confirm/{user_id}", data=payload)
    
    def get_analytics_csv(self, token: str | None = None) -> ApiResult:
        return self._request("GET", "analytics/export/csv", token=token)

    def get_tournament_bracket(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", f"tournament/{tournament_id}/bracket", token=token)

    def save_tournament_planning(self, tournament_id: int, token: str, format_value: str, stage_type: str) -> ApiResult:
        return self._request("POST", f"tournament/{tournament_id}/planning", data={"format": format_value, "stageType": stage_type}, token=token)

    def get_prize_pool(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", f"tournament/{tournament_id}/prize-pool", token=token)

    def set_tournament_stage(self, tournament_id: int, token: str, status: str, stage: str | None = None) -> ApiResult:
        payload = {"status": status}
        if stage:
            payload["stage"] = stage
        return self._request("POST", f"tournament/{tournament_id}/stage", data=payload, token=token)

    def set_match_stream(self, match_id: int | str, stream_url: str, token: str, stream_status: str = "linked") -> ApiResult:
        return self._request("PUT", f"matches/{match_id}/stream", data={"streamUrl": stream_url, "streamStatus": stream_status}, token=token)

    def open_mvp_voting(self, tournament_id: int, token: str, is_open: bool = True) -> ApiResult:
        return self._request("POST", "mvp/open", data={"tournamentId": tournament_id, "isOpen": is_open}, token=token)

    def distribute_prize_pool(self, tournament_id: int, token: str) -> ApiResult:
        return self._request("POST", f"tournament/{tournament_id}/prize-pool/distribute", token=token)

    def mark_prizes_paid(self, tournament_id: int, token: str) -> ApiResult:
        return self._request("POST", f"tournament/{tournament_id}/prize-pool/mark-paid", token=token)

    def get_tournament_streams(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", f"streams/tournament/{tournament_id}", token=token)

    def set_prize_payouts(self, tournament_id: int, token: str, payouts: list[dict]) -> ApiResult:
        return self._request("POST", f"tournament/{tournament_id}/prize-pool/payouts", data={"payouts": payouts}, token=token)

    def get_nominees(self) -> ApiResult:
        return self._request("GET", "voting/nominees")

    def vote(self, nominee_id: int, session_id: str | None, ip_address: str) -> ApiResult:
        return self._request("POST", "voting/vote", data={"nomineeId": nominee_id, "voterSession": session_id or "", "voterIp": ip_address})

    def has_voted(self, session_id: str) -> ApiResult:
        return self._request("GET", f"voting/hasvoted/{session_id}")

    def health_check(self) -> bool:
        return self._request("GET", "health").ok

    def verify_faceit_account(self, user_id: int, nickname: str, token: str) -> ApiResult:
        return self._request(
            "POST",
            f"integrations/faceit/verify/{user_id}",
            data={"faceitNickname": nickname},
            token=token,
        )

    def unlink_faceit(self, user_id: int, token: str) -> "ApiResult":
        return self._request("DELETE", f"integrations/faceit/unlink/{user_id}", token=token)

    def esports_player(self, nickname: str, game: str = "counterstrike") -> ApiResult:
        return self._request("GET", "esports/player", params={"nickname": nickname, "game": game})

    def esports_tournament_streams(self, query: str, game: str = "counterstrike") -> ApiResult:
        return self._request("GET", "esports/tournament/streams", params={"query": query, "game": game})

    def esports_diagnostics(self) -> ApiResult:
        return self._request("GET", "esports/diagnostics")


api_client = CSharpApiClient()
