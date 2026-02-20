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
    match_id = forms.IntegerField(min_value=1, label='ID матча', widget=forms.NumberInput(attrs={'class': 'form-control'}))
    score_a = forms.IntegerField(min_value=0, label='Счёт команды A', widget=forms.NumberInput(attrs={'class': 'form-control'}))
    score_b = forms.IntegerField(min_value=0, label='Счёт команды B', widget=forms.NumberInput(attrs={'class': 'form-control'}))


class RegistrationForm(forms.Form):
    ROLE_CHOICES = [
        ('player', 'Игрок'),
        ('captain', 'Капитан'),
    ]

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
        label='Роль',
        widget=forms.Select(attrs={'class': 'form-select'})
    )

    def clean(self):
        cleaned = super().clean()
        password = cleaned.get('password')
        password_confirm = cleaned.get('password_confirm')
        if password and len(password) < 8:
            self.add_error('password', 'Пароль должен содержать не менее 8 символов.')
        if password and password_confirm and password != password_confirm:
            self.add_error('password_confirm', 'Пароли не совпадают.')
        return cleaned
