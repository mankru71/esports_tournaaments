#!/bin/bash
set -e

# Create .env if missing
if [ ! -f .env ]; then
    echo "Creating .env file from template..."
    cp .env.example .env
    echo "⚠  Please edit .env with your real settings before continuing!"
    exit 1
fi

# Sanity checks
command -v docker >/dev/null 2>&1 || { echo "Docker is not installed."; exit 1; }

echo "🔨 Building and starting containers..."
docker compose up --build -d

echo ""
echo "⏳  Waiting for services... (first boot may take 2-3 min)"
echo "   Nginx starts immediately; backend services warm up behind it."
echo "   Watch progress: docker compose logs -f"
echo ""
echo "======================================================"
echo "  🌐  Frontend (via Nginx):  http://localhost"
echo "  📡  C# API (via Nginx):    http://localhost/api"
echo "  📖  Swagger UI:            http://localhost/api/swagger"
echo "  🐘  PostgreSQL (host):     localhost:5433"
echo "  🔴  Redis (host):          localhost:6379"
echo ""
echo "  docker compose ps       — service status"
echo "  docker compose logs -f  — tail all logs"
echo "  docker compose down     — stop everything"
echo "======================================================"
