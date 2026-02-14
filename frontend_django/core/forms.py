from django import forms


class LoginForm(forms.Form):
    email = forms.EmailField(label='Email')
    password = forms.CharField(label='Пароль', widget=forms.PasswordInput)


class MatchResultForm(forms.Form):
    match_id = forms.IntegerField(min_value=1, label='ID матча')
    score_a = forms.IntegerField(min_value=0, label='Счет Team A')
    score_b = forms.IntegerField(min_value=0, label='Счет Team B')


class RegistrationForm(forms.Form):
    MODE_CHOICES = [
        ('team', 'Команда'),
        ('solo', 'Игрок'),
    ]

    mode = forms.ChoiceField(choices=MODE_CHOICES, label='Кто регистрируется', widget=forms.RadioSelect)
    contact_name = forms.CharField(label='Контактное имя', max_length=80)
    email = forms.EmailField(label='Email')
    team_name = forms.CharField(label='Название команды', max_length=80, required=False)
    players = forms.CharField(
        label='Состав (никнеймы через запятую)',
        required=False,
        widget=forms.Textarea(attrs={'rows': 3, 'placeholder': 'Например: s1mple, ZywOo, NiKo'}),
    )
    logo = forms.FileField(label='Логотип (необязательно)', required=False)

    def clean(self):
        cleaned = super().clean()
        mode = cleaned.get('mode')
        if mode == 'team':
            if not cleaned.get('team_name'):
                self.add_error('team_name', 'Укажи название команды.')
            if not cleaned.get('players'):
                self.add_error('players', 'Укажи хотя бы несколько игроков.')
        return cleaned
