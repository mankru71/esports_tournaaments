FROM python:3.11-slim

# Устанавливаем системные зависимости
RUN apt-get update && apt-get install -y \
    gcc \
    postgresql-client \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Устанавливаем Python зависимости
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Копируем код приложения
COPY . .

# Создаем пользователя для безопасности
RUN useradd -m -u 1000 django && chown -R django /app
USER django

# Настраиваем порт
EXPOSE 8000

# Команда запуска (переопределяется в docker-compose)
CMD ["python", "manage.py", "runserver", "0.0.0.0:8000"]