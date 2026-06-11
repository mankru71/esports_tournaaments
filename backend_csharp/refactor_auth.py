import os
import re

controllers_dir = r"d:\esports_tournaaments\backend_csharp\Controllers"
hubs_dir = r"d:\esports_tournaaments\backend_csharp\Hubs"

def process_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    original_content = content

    # Add using Infrastructure; if not there
    if 'using Infrastructure;' not in content and 'User.GetUserId()' in content.replace('AuthTokenHelper.GetUserId(Request)', 'User.GetUserId()'):
        pass # Actually, we can just replace and add using. Or use the fully qualified Infrastructure.ClaimsPrincipalExtensions if needed. But it's in Infrastructure namespace.
    
    # Simple replacements
    content = content.replace("AuthTokenHelper.GetUserId(Request)", "User.GetUserId()")
    content = content.replace("Infrastructure.AuthTokenHelper.GetUserId(Request)", "User.GetUserId()")

    # Regex for IsInAnyRole
    def role_replacer(match):
        # match.group(1) is the roles string like '"admin", "judge"'
        roles_str = match.group(1)
        roles = [r.strip().strip('"') for r in roles_str.split(',')]
        checks = [f'User.IsInRole("{r}")' for r in roles if r]
        return " || ".join(checks) if checks else "false"

    content = re.sub(r'AuthTokenHelper\.IsInAnyRole\(Request,\s*(.*?)\)', role_replacer, content)
    content = re.sub(r'Infrastructure\.AuthTokenHelper\.IsInAnyRole\(Request,\s*(.*?)\)', role_replacer, content)

    # In AuthController there is `AuthTokenHelper.ParseClaims(AuthTokenHelper.GetBearerToken(Request))`
    # and `AuthTokenHelper.GetUserId(Request)`
    # The /me endpoint in AuthController does:
    # var claims = AuthTokenHelper.ParseClaims(AuthTokenHelper.GetBearerToken(Request));
    # string? currentRole = claims.TryGetValue("role", out var r) ? r : null;
    content = content.replace(
        'var claims = AuthTokenHelper.ParseClaims(AuthTokenHelper.GetBearerToken(Request));\n        string? currentRole = claims.TryGetValue("role", out var r) ? r : null;',
        'string? currentRole = User.GetRole();'
    )

    if content != original_content:
        # ensure using Infrastructure;
        if 'using Infrastructure;' not in content:
            content = 'using Infrastructure;\n' + content
        # ensure [Authorize] on class or we just use User which is inherited from ControllerBase
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(content)
        print(f"Updated {filepath}")

for root, _, files in os.walk(controllers_dir):
    for f in files:
        if f.endswith('.cs'):
            process_file(os.path.join(root, f))

for root, _, files in os.walk(hubs_dir):
    for f in files:
        if f.endswith('.cs'):
            process_file(os.path.join(root, f))
