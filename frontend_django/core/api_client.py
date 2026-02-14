import requests
import json
import logging
from django.conf import settings
from django.core.cache import cache
from functools import wraps

logger = logging.getLogger(__name__)

class CSharpApiClient:
    """Клиент для взаимодействия с C# API"""
    
    def __init__(self):
        self.base_url = settings.C_SHARP_API['BASE_URL']
        self.timeout = settings.C_SHARP_API.get('TIMEOUT', 30)
        self.session = requests.Session()
        
    def _handle_request(self, method, endpoint, data=None, params=None):
        """Обработка HTTP запросов"""
        url = f"{self.base_url}/{endpoint.lstrip('/')}"
        
        try:
            response = self.session.request(
                method=method,
                url=url,
                json=data,
                params=params,
                timeout=self.timeout,
                headers={'Content-Type': 'application/json'}
            )
            response.raise_for_status()
            return response.json() if response.content else None
        except requests.exceptions.Timeout:
            logger.error(f"C# API timeout: {url}")
            return None
        except requests.exceptions.RequestException as e:
            logger.error(f"C# API error: {e}")
            return None
    
    def _cache_wrapper(self, func, cache_key, timeout=300):
        """Декоратор для кэширования"""
        @wraps(func)
        def wrapper(*args, **kwargs):
            if settings.C_SHARP_API.get('ENABLE_CACHE', True):
                cached_result = cache.get(cache_key)
                if cached_result:
                    logger.debug(f"Cache hit: {cache_key}")
                    return cached_result
            
            result = func(*args, **kwargs)
            
            if result and settings.C_SHARP_API.get('ENABLE_CACHE', True):
                cache.set(cache_key, result, timeout)
            
            return result
        return wrapper
    
    # Методы API
    
    def get_tournaments(self):
        """Получить список турниров"""
        cache_key = 'csharp_api_tournaments'
        func = lambda: self._handle_request('GET', 'api/tournament')
        return self._cache_wrapper(func, cache_key)()
    
    def get_tournament(self, tournament_id):
        """Получить детали турнира"""
        cache_key = f'csharp_api_tournament_{tournament_id}'
        func = lambda: self._handle_request('GET', f'api/tournament/{tournament_id}')
        return self._cache_wrapper(func, cache_key, timeout=60)()
    
    def get_stats(self):
        """Получить статистику"""
        cache_key = 'csharp_api_stats'
        func = lambda: self._handle_request('GET', 'api/tournament/stats')
        return self._cache_wrapper(func, cache_key, timeout=30)()
    
    def get_nominees(self):
        """Получить список номинантов"""
        cache_key = 'csharp_api_nominees'
        func = lambda: self._handle_request('GET', 'api/voting/nominees')
        return self._cache_wrapper(func, cache_key)()
    
    def vote(self, nominee_id, session_id, ip_address):
        """Проголосовать за номинанта"""
        data = {
            'NomineeId': nominee_id,
            'VoterSession': session_id,
            'VoterIp': ip_address
        }
        return self._handle_request('POST', 'api/voting/vote', data=data)
    
    def has_voted(self, session_id):
        """Проверить, голосовал ли пользователь"""
        return self._handle_request('GET', f'api/voting/hasvoted/{session_id}')
    
    def health_check(self):
        """Проверить доступность API"""
        try:
            response = self.session.get(f"{self.base_url}/api/health", timeout=5)
            return response.status_code == 200
        except:
            return False

# Синглтон экземпляр клиента
api_client = CSharpApiClient()