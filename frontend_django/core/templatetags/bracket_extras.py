import json
from django import template
from django.utils.safestring import mark_safe  # <-- Обязательно импортируем mark_safe

register = template.Library()

@register.filter(is_safe=True, name="safe_json")
def safe_json(value):
    try:
        raw = json.dumps(value, ensure_ascii=False, default=str)
        # Оборачиваем результат в mark_safe
        return mark_safe(raw.replace("</", "<\\/"))
    except (TypeError, ValueError):
        return mark_safe("[]")