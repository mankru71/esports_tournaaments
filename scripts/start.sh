#!/bin/bash

# Создаем .env файл если его нет
if [ ! -f .env ]; then
    echo "Creating .env file from template..."
    cp .env.example .env
    echo "Please edit .env file with your settings!"
fi

# Проверяем Docker и Docker Compose
if ! command -v docker &> /dev/null; then
    echo "Docker is not installed. Please install Docker first."
    exit 1
fi

if ! command -v docker-compose &> /dev/null; then
    echo "Docker Compose is not installed. Please install Docker Compose."
    exit 1
fi

# Строим и запускаем контейнеры
echo "Building and starting containers..."
docker-compose up --build -d

echo "Waiting for services to start..."
sleep 10

# Проверяем статус
echo "Checking services status..."
docker-compose ps

echo "======================================"
echo "Services are running!"
echo ""
echo "Django Frontend: http://localhost:8000"
echo "C# API: http://localhost:5000"
echo "C# API Swagger: http://localhost:5000/swagger"
echo "PostgreSQL: localhost:5432"
echo "Redis: localhost:6379"
echo ""
echo "To view logs: docker-compose logs -f"
echo "To stop: docker-compose down"
echo "======================================"