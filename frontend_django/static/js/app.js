(function () {
  "use strict";

  const THEME_KEY = "theme";
  const FAVORITES_KEY = "favoriteTournaments";

  // Твои оригинальные цветовые палитры сохранены
  const PALETTES = {
    dark: {
      "--bg": "#101114",
      "--surface": "#17191d",
      "--surface-2": "#1f2227",
      "--text": "#f2f1ed",
      "--muted": "#9c9a92",
      "--border": "rgba(255,255,255,.09)",
      "--accent": "#c8a96a",
      "--accent-2": "#dfc48a",
      "--accent-soft": "rgba(200,169,106,.12)",
      "--shadow": "0 14px 36px rgba(0,0,0,.22)",
      "--table-striped-bg": "rgba(255,255,255,.025)",
    },
    light: {
      "--bg": "#f5f2ea",
      "--surface": "#ffffff",
      "--surface-2": "#f0ede6",
      "--text": "#1a1c1f",
      "--muted": "#68645d",
      "--border": "rgba(26,28,31,.11)",
      "--accent": "#9b7437",
      "--accent-2": "#6f542d",
      "--accent-soft": "rgba(155,116,55,.10)",
      "--shadow": "0 14px 34px rgba(31,31,31,.08)",
      "--table-striped-bg": "rgba(26,28,31,.025)",
    },
  };

  function readJson(key, fallback) {
    try {
      const raw = localStorage.getItem(key);
      return raw ? JSON.parse(raw) : fallback;
    } catch {
      return fallback;
    }
  }

  function writeJson(key, value) {
    try { localStorage.setItem(key, JSON.stringify(value)); } catch {}
  }

  function safeGetTheme() {
    try { return localStorage.getItem(THEME_KEY) === "light" ? "light" : "dark"; } catch { return "dark"; }
  }

  function applyTheme(theme) {
    const normalized = theme === "light" ? "light" : "dark";
    const root = document.documentElement;
    root.setAttribute("data-theme", normalized);
    root.setAttribute("data-bs-theme", normalized);
    root.style.colorScheme = normalized === "dark" ? "dark" : "light";
    Object.entries(PALETTES[normalized]).forEach(([key, value]) => root.style.setProperty(key, value));
    try { localStorage.setItem(THEME_KEY, normalized); } catch {}
    const meta = document.getElementById("meta-theme-color");
    if (meta) meta.setAttribute("content", normalized === "light" ? "#f5f2ea" : "#101114");
  }

  function syncToggleUi() {
    const theme = document.documentElement.getAttribute("data-theme") === "light" ? "light" : "dark";
    const label = document.getElementById("themeToggleLabel");
    if (label) label.textContent = theme === "light" ? "Светлая" : "Тёмная";
  }

  function getFavorites() {
    return readJson(FAVORITES_KEY, []).map(String);
  }

  function setFavorites(items) {
    writeJson(FAVORITES_KEY, Array.from(new Set(items.map(String))));
  }

  function updateFavoriteButtons() {
    const favorites = new Set(getFavorites());
    document.querySelectorAll("[data-favorite-tournament]").forEach((button) => {
      const id = String(button.getAttribute("data-favorite-tournament"));
      const active = favorites.has(id);
      button.textContent = active ? "В избранном" : "В избранное";
      button.classList.toggle("active", active);
      button.setAttribute("aria-pressed", active ? "true" : "false");
    });
  }

  function applyFavoritesFilter(mode) {
    const favorites = new Set(getFavorites());
    const rows = Array.from(document.querySelectorAll("[data-tournament-row]"));
    if (!rows.length) return;

    let visible = 0;
    rows.forEach((row) => {
      const id = String(row.getAttribute("data-tournament-row"));
      const hide = mode === "only" && !favorites.has(id);
      row.classList.toggle("is-hidden-by-favorite", hide);
      if (!hide) visible += 1;
    });

    const empty = document.querySelector("[data-favorites-empty]");
    if (empty) empty.classList.toggle("d-none", !(mode === "only" && visible === 0));
  }

  function initFavorites() {
    updateFavoriteButtons();

    document.querySelectorAll("[data-favorite-tournament]").forEach((button) => {
      button.addEventListener("click", () => {
        const id = String(button.getAttribute("data-favorite-tournament"));
        const favorites = new Set(getFavorites());
        if (favorites.has(id)) favorites.delete(id); else favorites.add(id);
        setFavorites(Array.from(favorites));
        updateFavoriteButtons();
        const activeFilter = document.querySelector("[data-show-favorites].active");
        if (activeFilter) applyFavoritesFilter(activeFilter.getAttribute("data-show-favorites"));
      });
    });

    document.querySelectorAll("[data-show-favorites]").forEach((button) => {
      button.addEventListener("click", () => {
        document.querySelectorAll("[data-show-favorites]").forEach((b) => b.classList.remove("active"));
        button.classList.add("active");
        applyFavoritesFilter(button.getAttribute("data-show-favorites"));
      });
    });
  }

  function initCopyButtons() {
    document.querySelectorAll("[data-copy-current-url]").forEach((button) => {
      button.addEventListener("click", async () => {
        try {
          await navigator.clipboard.writeText(window.location.href);
          const oldText = button.textContent;
          button.textContent = "Ссылка скопирована";
          button.classList.add("text-success", "border-success"); // Добавлен легкий визуальный эффект успеха
          
          setTimeout(() => { 
            button.textContent = oldText; 
            button.classList.remove("text-success", "border-success");
          }, 1800);
        } catch {
          window.prompt("Скопируйте ссылку", window.location.href);
        }
      });
    });
  }

  // --- НОВАЯ ФУНКЦИЯ: Плавные анимации при скролле ---
  function initScrollAnimations() {
    const observerOptions = {
      threshold: 0.05, // Элемент начнет появляться, когда хотя бы 5% его видно на экране
      rootMargin: "0px 0px -40px 0px" // Небольшой отступ снизу для красоты
    };

    const observer = new IntersectionObserver((entries, observer) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) {
          entry.target.classList.add('animate-in');
          observer.unobserve(entry.target); // Отключаем наблюдение, чтобы не анимировать повторно
        }
      });
    }, observerOptions);

    // Находим все блоки, которые нужно анимировать
    const animatedElements = document.querySelectorAll(
      '.card, .section-panel, .table-panel, .page-hero, .empty-state'
    );

    // Задаем каждому элементу каскадную задержку, чтобы они выплывали друг за другом
    animatedElements.forEach((el, index) => {
      // Максимум 10 элементов в каскаде, чтобы анимация не затягивалась надолго
      const delay = (index % 10) * 0.07; 
      el.style.animationDelay = `${delay}s`;
      observer.observe(el);
    });
  }

  // Инициализация темы до полной загрузки DOM, чтобы избежать мерцания
  applyTheme(safeGetTheme());

  document.addEventListener("DOMContentLoaded", function () {
    // Авто-скрытие уведомлений
    document.querySelectorAll("[data-auto-dismiss='true']").forEach((el) => {
      setTimeout(() => {
        el.classList.remove("show");
        el.classList.add("fade");
      }, 4500);
    });

    // Год в футере
    const yearEl = document.getElementById("footer-year");
    if (yearEl) yearEl.textContent = String(new Date().getFullYear());

    // Тумблер переключения темы
    syncToggleUi();
    const btn = document.getElementById("themeToggle");
    if (btn) {
      btn.addEventListener("click", function () {
        const current = document.documentElement.getAttribute("data-theme") === "light" ? "light" : "dark";
        applyTheme(current === "light" ? "dark" : "light");
        syncToggleUi();
      });
    }

    // Запуск всех модулей
    initFavorites();
    initCopyButtons();
    initScrollAnimations(); // Запускаем наши красивые анимации!
  });
  // --- СОХРАНЕНИЕ ПОЗИЦИИ СКРОЛЛА ПРИ ОТПРАВКЕ ФОРМ ---
  document.addEventListener("DOMContentLoaded", () => {
    const scrollKey = "pageScrollPos";
    const urlKey = "pageScrollUrl";

    // 1. Возвращаем скролл на место, если мы перезагрузили ту же самую страницу
    const savedPos = sessionStorage.getItem(scrollKey);
    const savedUrl = sessionStorage.getItem(urlKey);
    const currentUrl = window.location.pathname + window.location.search;

    if (savedPos && savedUrl === currentUrl) {
      // setTimeout нужен, чтобы браузер успел отрисовать DOM перед прокруткой
      setTimeout(() => window.scrollTo(0, parseInt(savedPos, 10)), 10);
    }

    // Очищаем кэш скролла, чтобы при переходе по обычным ссылкам страница открывалась сверху
    sessionStorage.removeItem(scrollKey);
    sessionStorage.removeItem(urlKey);
  });

  // 2. Сохраняем позицию прямо перед отправкой любой формы на сайте
  document.addEventListener("submit", () => {
    sessionStorage.setItem("pageScrollPos", window.scrollY);
    sessionStorage.setItem("pageScrollUrl", window.location.pathname + window.location.search);
  });
})();