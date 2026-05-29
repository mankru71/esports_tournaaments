"""
Custom Django template filters for the Arena Control project.

Usage in templates:
  {% load bracket_extras %}
  {{ bracket.matches|safe_json }}
"""
import json
from django import template

register = template.Library()


@register.filter(is_safe=True, name="safe_json")
def safe_json(value):
    """
    Serialise a Python object to a JSON string that is safe to embed
    inside a <script> tag.  Escapes </script> sequences to prevent
    early tag termination and XSS.
    """
    try:
        raw = json.dumps(value, ensure_ascii=False, default=str)
        # Prevent </script> from ending the tag prematurely
        return raw.replace("</", "<\\/")
    except (TypeError, ValueError):
        return "[]"
