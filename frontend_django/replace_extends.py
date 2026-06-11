import os

template_dir = r"d:\esports_tournaaments\frontend_django\core\templates"
for filename in os.listdir(template_dir):
    if filename.endswith(".html") and filename not in ["base.html", "base_partial.html"]:
        filepath = os.path.join(template_dir, filename)
        with open(filepath, "r", encoding="utf-8") as f:
            content = f.read()
        
        # Replace simple single quotes
        if "{% extends 'base.html' %}" in content:
            content = content.replace("{% extends 'base.html' %}", '{% extends request.is_htmx|yesno:"base_partial.html,base.html" %}')
            with open(filepath, "w", encoding="utf-8") as f:
                f.write(content)
        elif '{% extends "base.html" %}' in content:
            content = content.replace('{% extends "base.html" %}', '{% extends request.is_htmx|yesno:"base_partial.html,base.html" %}')
            with open(filepath, "w", encoding="utf-8") as f:
                f.write(content)
print("Replacement complete.")
