import re

valid_targets = {'dashboard', 'login', 'logout', 'profile', 'steam_callback', 'faceit_callback', 'verify_email', 'pro_tournaments', 'pro_tournament_detail', 'pro_match_center', 'pro_mvp', 'pro_analytics', 'pro_streams', 'pro_voting', 'pro_leaderboard', 'fantasy_draft', 'fantasy_draft_submit', 'fantasy_leaderboard', 'play_tournaments', 'play_tournament_detail', 'play_match_center', 'play_scouting', 'registration', 'smart_scouting', 'smart_scouting_swipe', 'play_teams'}

file_path = r'd:\esports_tournaaments\frontend_django\core\views.py'

with open(file_path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

for i, line in enumerate(lines):
    match = re.search(r'redirect\([\'\"](.*?)[\'\"]', line)
    if match:
        target = match.group(1)
        if target not in valid_targets and not target.startswith('/') and not target.startswith('http'):
            print(f'Line {i+1}: {line.strip()}')
