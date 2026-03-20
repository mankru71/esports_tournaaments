from django import forms


class LoginForm(forms.Form):
    email = forms.EmailField(
        label='Электронная почта',
        widget=forms.EmailInput(attrs={'class': 'form-control', 'placeholder': 'player@arena.gg'})
    )
    password = forms.CharField(
        label='Пароль',
        widget=forms.PasswordInput(attrs={'class': 'form-control', 'placeholder': 'Введите пароль'})
    )


class MatchResultForm(forms.Form):
    match_id = forms.CharField(label='ID матча', widget=forms.TextInput(attrs={'class': 'form-control'}))
    score_a = forms.IntegerField(min_value=0, label='Счёт команды A', widget=forms.NumberInput(attrs={'class': 'form-control'}))
    score_b = forms.IntegerField(min_value=0, label='Счёт команды B', widget=forms.NumberInput(attrs={'class': 'form-control'}))


class RegistrationForm(forms.Form):
    ROLE_CHOICES = [
        ('captain', 'Капитан'),
        ('player', 'Игрок'),
        ('judge', 'Судья'),
        ('admin', 'Администратор'),
        ('viewer', 'Зритель'),
    ]

    nickname = forms.CharField(
        label='Ник',
        max_length=32,
        widget=forms.TextInput(attrs={'class': 'form-control', 'placeholder': 's1mple', 'autocomplete': 'nickname'})
    )
    email = forms.EmailField(
        label='Электронная почта',
        widget=forms.EmailInput(attrs={'class': 'form-control', 'placeholder': 'nickname@example.com'})
    )
    password = forms.CharField(
        label='Пароль',
        widget=forms.PasswordInput(attrs={'class': 'form-control', 'placeholder': 'Минимум 8 символов'})
    )
    password_confirm = forms.CharField(
        label='Повторите пароль',
        widget=forms.PasswordInput(attrs={'class': 'form-control', 'placeholder': 'Повторите пароль'})
    )
    role = forms.ChoiceField(
        choices=ROLE_CHOICES,
        initial='captain',
        label='Роль',
        widget=forms.Select(attrs={'class': 'form-select'})
    )

    def clean(self):
        cleaned = super().clean()
        nickname = (cleaned.get('nickname') or '').strip()
        cleaned['nickname'] = nickname

        password = cleaned.get('password')
        password_confirm = cleaned.get('password_confirm')
        if password and len(password) < 8:
            self.add_error('password', 'Пароль должен содержать не менее 8 символов.')
        if password and password_confirm and password != password_confirm:
            self.add_error('password_confirm', 'Пароли не совпадают.')

        if nickname:
            import re
            if len(nickname) < 2 or len(nickname) > 32:
                self.add_error('nickname', 'Ник должен быть длиной 2–32 символа.')
            elif not re.match(r'^[A-Za-z0-9._-]+$', nickname):
                self.add_error('nickname', 'Ник может содержать только латинские буквы, цифры и символы . _ -')
        else:
            self.add_error('nickname', 'Укажите ник.')

        return cleaned


class TeamCreateForm(forms.Form):
    name = forms.CharField(
        label='Название команды',
        max_length=120,
        widget=forms.TextInput(attrs={'class': 'form-control', 'placeholder': 'Team Phoenix'})
    )


class TeamPlayerForm(forms.Form):
    team_id = forms.IntegerField(widget=forms.HiddenInput())
    nickname = forms.CharField(
        label='Ник игрока',
        max_length=100,
        widget=forms.TextInput(attrs={'class': 'form-control', 'placeholder': 's1mple'})
    )
    rating = forms.DecimalField(
        label='Рейтинг',
        required=False,
        min_value=0,
        decimal_places=2,
        max_digits=8,
        widget=forms.NumberInput(attrs={'class': 'form-control', 'placeholder': '2450'})
    )
    game = forms.CharField(
        label='Дисциплина',
        required=False,
        max_length=50,
        widget=forms.TextInput(attrs={'class': 'form-control', 'placeholder': 'counterstrike'})
    )


class ProfileEditForm(forms.Form):
    nickname = forms.CharField(
        label='Ник',
        max_length=32,
        widget=forms.TextInput(attrs={'class': 'form-control', 'placeholder': 's1mple'})
    )
    bio = forms.CharField(
        label='О себе',
        required=False,
        widget=forms.Textarea(attrs={'class': 'form-control', 'rows': 4, 'placeholder': 'Капитан команды, предпочитаю CS2 и LAN-турниры.'})
    )


class RatingVerifyForm(forms.Form):
    provider = forms.ChoiceField(
        label='Провайдер рейтинга',
        choices=(('faceit', 'Faceit (mock)'), ('steam', 'Steam (mock)')),
        widget=forms.Select(attrs={'class': 'form-select'})
    )
    profile_url = forms.URLField(
        label='Ссылка на профиль',
        widget=forms.URLInput(attrs={'class': 'form-control', 'placeholder': 'https://www.faceit.com/... или https://steamcommunity.com/...'})
    )


class TournamentCreateForm(forms.Form):
    name = forms.CharField(label='Название турнира', max_length=120, widget=forms.TextInput(attrs={'class': 'form-control'}))
    game = forms.CharField(label='Дисциплина', max_length=60, initial='counterstrike', widget=forms.TextInput(attrs={'class': 'form-control'}))
    prize_pool = forms.DecimalField(label='Призовой фонд', min_value=0, decimal_places=2, max_digits=12, initial=10000, widget=forms.NumberInput(attrs={'class': 'form-control'}))
    max_participants = forms.IntegerField(label='Макс. участников', min_value=2, max_value=64, initial=8, widget=forms.NumberInput(attrs={'class': 'form-control'}))
    start_date = forms.DateField(label='Дата старта', widget=forms.DateInput(attrs={'class': 'form-control', 'type': 'date'}))
    format = forms.ChoiceField(label='Формат', choices=(('single_elimination', 'Single elimination'), ('group_stage', 'Group stage')), widget=forms.Select(attrs={'class': 'form-select'}))
    stage_type = forms.ChoiceField(label='Тип этапа', choices=(('single', 'Single bracket'), ('groups', 'Groups')), widget=forms.Select(attrs={'class': 'form-select'}))
