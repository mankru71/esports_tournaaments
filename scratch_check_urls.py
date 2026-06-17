import re
import os

valid_targets = {'dashboard', 'login', 'logout', 'profile', 'steam_callback', 'faceit_callback', 'verify_email', 'pro_tournaments', 'pro_tournament_detail', 'pro_match_center', 'pro_mvp', 'pro_analytics', 'pro_streams', 'pro_voting', 'pro_leaderboard', 'fantasy_draft', 'fantasy_draft_submit', 'fantasy_leaderboard', 'play_tournaments', 'play_tournament_detail', 'play_match_center', 'play_scouting', 'registration', 'smart_scouting', 'smart_scouting_swipe', 'play_teams'}

templates_dir = r'd:\esports_tournaaments\frontend_django\core\templates'

for root, dirs, files in os.walk(templates_dir):
    for file in files:
        if file.endswith('.html'):
            file_path = os.path.join(root, file)
            with open(file_path, 'r', encoding='utf-8') as f:
                content = f.read()
                
            matches = re.findall(r'{%\s*url\s+[\'\"](.*?)[\'\"]', content)
            for match in matches:
                if match not in valid_targets:
                    print(f'Invalid URL name "{match}" in {file}')
