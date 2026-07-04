"""
ml-service — микросервис прогнозов исходов матчей.

Внутренний контейнер (порт наружу не публикуется): C#-бэкенд ходит сюда
по Docker DNS (http://ml-service:8001) через MatchPredictionService.

Модель: вероятность победы по разнице средних Faceit Elo команд
(логистическая Elo-кривая, классическая формула с делителем 400).
Состав фич, который присылает бэкенд, описан в TeamFeatures.
"""

import logging
import math

from fastapi import FastAPI
from pydantic import BaseModel

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("ml-service")

MODEL_NAME = "elo-v1"
DEFAULT_RATING = 1500.0

app = FastAPI(title="Arena Control — Match Predictor")


class TeamFeatures(BaseModel):
    teamId: int
    name: str
    avgRating: float | None = None
    playersCount: int = 0


class PredictRequest(BaseModel):
    matchId: int
    teamA: TeamFeatures
    teamB: TeamFeatures


class PredictResponse(BaseModel):
    probA: float
    probB: float
    model: str


def predict_elo(team_a: TeamFeatures, team_b: TeamFeatures) -> float:
    """Elo-вероятность победы команды A по разнице средних рейтингов."""
    rating_a = team_a.avgRating if team_a.avgRating is not None else DEFAULT_RATING
    rating_b = team_b.avgRating if team_b.avgRating is not None else DEFAULT_RATING
    if rating_a < 10.0:
        rating_a = rating_a * 2000.0
    if rating_b < 10.0:
        rating_b = rating_b * 2000.0
    return 1.0 / (1.0 + math.pow(10.0, (rating_b - rating_a) / 400.0))


@app.get("/health")
def health() -> dict:
    return {"status": "ok", "model": MODEL_NAME}


@app.post("/predict", response_model=PredictResponse)
def predict(request: PredictRequest) -> PredictResponse:
    prob_a = predict_elo(request.teamA, request.teamB)
    # Не показываем 0%/100% — у аутсайдера всегда есть шанс
    prob_a = min(max(prob_a, 0.01), 0.99)
    logger.info(
        "predict match=%s %s(%.0f) vs %s(%.0f) -> %.3f",
        request.matchId,
        request.teamA.name, request.teamA.avgRating or DEFAULT_RATING,
        request.teamB.name, request.teamB.avgRating or DEFAULT_RATING,
        prob_a,
    )
    return PredictResponse(probA=round(prob_a, 4), probB=round(1.0 - prob_a, 4), model=MODEL_NAME)
