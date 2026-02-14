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
            logger.error("C# API connection error: %s", exc)
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
            pass

        if response.status_code == 401:
            return ApiResult(ok=False, error={"code": "unauthorized", "message": "Требуется вход", "details": body})
        if response.status_code == 403:
            return ApiResult(ok=False, error={"code": "forbidden", "message": "Недостаточно прав", "details": body})
        if response.status_code >= 500:
            return ApiResult(ok=False, error={"code": "server_error", "message": "API недоступно", "details": body})

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

    def register(self, email: str, password: str, role: str = "captain") -> ApiResult:
        return self._request("POST", "auth/register", data={"email": email, "password": password, "role": role})

    def me(self, token: str) -> ApiResult:
        return self._request("GET", "auth/me", token=token)

    # Tournaments
    def get_tournaments(self, token: str | None = None) -> ApiResult:
        return self._request("GET", "tournament", token=token)

    def get_tournament(self, tournament_id: int, token: str | None = None) -> ApiResult:
        return self._request("GET", f"tournament/{tournament_id}", token=token)

    def get_stats(self) -> ApiResult:
        return self._request("GET", "tournament/stats")

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
