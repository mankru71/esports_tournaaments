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
