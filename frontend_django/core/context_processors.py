def user_context(request):
    from core.views import _read_current_user, _role_flags
    user = _read_current_user(request)
    return _role_flags(user)
