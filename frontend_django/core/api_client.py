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
    """Клиент для взаимодействия с C# API с единым форматом ошибок."""

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

        logger.info("C# API request: %s %s", method.upper(), url)
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

        logger.info("C# API response: %s %s -> %s", method.upper(), url, response.status_code)
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
            pass

        # 401 почти везде означает «нужна авторизация».
        # Для /auth/login текст ошибки обрабатывается отдельно во view.
        if response.status_code == 401:
            return ApiResult(
                ok=False,
                error={
                    "code": "unauthorized",
                    "message": (body or {}).get("message") or "Требуется вход",
                    "details": body,
                },
            )
        if response.status_code == 403:
            return ApiResult(ok=False, error={"code": "forbidden", "message": "Недостаточно прав", "details": body})
        if response.status_code == 400:
            return ApiResult(ok=False, error={"code": "validation_error", "message": body.get("message") or body.get("title") or "Проверьте корректность данных", "details": body})
        if response.status_code == 409:
            return ApiResult(ok=False, error={"code": "conflict", "message": body.get("message") or "Пользователь уже существует", "details": body})
        if response.status_code >= 500:
            return ApiResult(ok=False, error={"code": "server_error", "message": "Ошибка сервера API", "details": body})

        return ApiResult(
            ok=False,
            error={
                "code": f"http_{response.status_code}",
                "message": body.get("message") or body.get("title") or "Ошибка API",
                "details": body,
            },
        )

    def token_expired(self, token: str | None) -> bool:
        if not token:
            return True
        exp = self._decode_exp(token)
        if not exp:
            return False
        return exp <= int(time.time())

    # Auth
    def login(self, email: str, password: str) -> ApiResult:
        return self._request("POST", "auth/login", data={"email": email, "password": password})

    def register(self, email: str, password: str, nickname: str, role: str = "captain") -> ApiResult:
        return self._request("POST", "auth/register", data={"email": email, "password": password, "nickname": nickname, "role": role})

    def me(self, token: str) -> ApiResult:
        return self._request("GET", "auth/me", token=token)

    # Tournaments
    def get_tournaments(self, token: str | None = None) -> ApiResult:
        return self._request("GET", "tournament", token=token)

    def get_tournament(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", f"tournament/{tournament_id}", token=token)

    def get_stats(self) -> ApiResult:
        return self._request("GET", "tournament/stats")

    # Teams
    def get_teams(self, token: str | None = None) -> ApiResult:
        return self._request("GET", "teams", token=token)

    def create_team(self, name: str, token: str) -> ApiResult:
        return self._request("POST", "teams", data={"name": name}, token=token)

    def add_team_player(self, team_id: int, nickname: str, token: str) -> ApiResult:
        return self._request("POST", f"teams/{team_id}/players", data={"nickname": nickname}, token=token)

    def delete_team_player(self, team_id: int, player_id: int, token: str) -> ApiResult:
        return self._request("DELETE", f"teams/{team_id}/players/{player_id}", token=token)

    def delete_team(self, team_id: int, token: str) -> ApiResult:
        return self._request("DELETE", f"teams/{team_id}", token=token)

    # Matches / MVP / Streams / Analytics
    def get_matches(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", "matches", params={"tournamentId": tournament_id}, token=token)

    def update_match_result(self, match_id: int, score_a: int, score_b: int, token: str) -> ApiResult:
        return self._request("PUT", f"matches/{match_id}/result", data={"scoreA": score_a, "scoreB": score_b}, token=token)

    def get_mvp(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", "mvp/results", params={"tournamentId": tournament_id}, token=token)

    def vote_mvp(self, tournament_id: int, player_id: int, token: str) -> ApiResult:
        return self._request("POST", "mvp/vote", data={"tournamentId": tournament_id, "playerId": player_id}, token=token)

    def get_streams(self, token: str | None = None) -> ApiResult:
        return self._request("GET", "streams/status", token=token)

    def get_analytics(self, token: str | None = None) -> ApiResult:
        return self._request("GET", "analytics", token=token)

    # legacy voting
    def get_nominees(self) -> ApiResult:
        return self._request("GET", "voting/nominees")

    def vote(self, nominee_id: int, session_id: str | None, ip_address: str) -> ApiResult:
        return self._request(
            "POST",
            "voting/vote",
            data={"nomineeId": nominee_id, "voterSession": session_id or "", "voterIp": ip_address},
        )

    def has_voted(self, session_id: str) -> ApiResult:
        return self._request("GET", f"voting/hasvoted/{session_id}")

    def health_check(self) -> bool:
        result = self._request("GET", "health")
        return result.ok


api_client = CSharpApiClient()


    # Tournament applications
    def apply_to_tournament(self, tournament_id: int, team_id: int, token: str) -> ApiResult:
        return self._request("POST", f"tournament/{tournament_id}/applications", data={"teamId": team_id}, token=token)

    def my_tournament_applications(self, tournament_id: int, token: str) -> ApiResult:
        return self._request("GET", f"tournament/{tournament_id}/applications/my", token=token)

    # External esports data (Liquipedia)
    def esports_player(self, nickname: str, game: str = "counterstrike") -> ApiResult:
        return self._request("GET", "esports/player", params={"nickname": nickname, "game": game})

    def esports_tournament_streams(self, query: str, game: str = "counterstrike") -> ApiResult:
        return self._request("GET", "esports/tournament/streams", params={"query": query, "game": game})
