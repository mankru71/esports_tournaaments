import os
import re

controllers_dir = r"d:\esports_tournaaments\backend_csharp\Controllers"

def process_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    original_content = content
    content = content.replace("Infrastructure.User.IsInRole", "User.IsInRole")
    content = content.replace("Infrastructure.User.GetUserId", "User.GetUserId")
    content = content.replace("Infrastructure.AuthTokenHelper.GetUserId", "User.GetUserId")

    if content != original_content:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(content)
        print(f"Updated {filepath}")

for root, _, files in os.walk(controllers_dir):
    for f in files:
        if f.endswith('.cs'):
            process_file(os.path.join(root, f))
