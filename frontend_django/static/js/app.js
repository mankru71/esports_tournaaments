(function () {
  "use strict";

  const THEME_KEY = "theme";

  // ── Тема ────────────────────────────────────────────────────────────
  // Палитры живут ТОЛЬКО в CSS (app.css: :root / html[data-theme="light"]).
  // JS лишь переключает атрибут data-theme — никаких inline-переменных,
  // иначе появляется второй источник правды и рассинхрон при переключении.

  function safeGetTheme() {
    try { return localStorage.getItem(THEME_KEY) === "light" ? "light" : "dark"; } catch { return "dark"; }
  }

  function applyTheme(theme) {
    const normalized = theme === "light" ? "light" : "dark";
    const root = document.documentElement;
    root.setAttribute("data-theme", normalized);
    root.setAttribute("data-bs-theme", normalized);
    root.style.colorScheme = normalized === "dark" ? "dark" : "light";
    try { localStorage.setItem(THEME_KEY, normalized); } catch {}
    const meta = document.getElementById("meta-theme-color");
    if (meta) meta.setAttribute("content", normalized === "light" ? "#f5f2ea" : "#101114");
  }

  function syncToggleUi() {
    const theme = document.documentElement.getAttribute("data-theme") === "light" ? "light" : "dark";
    const label = document.getElementById("themeToggleLabel");
    if (label) label.textContent = theme === "light" ? "Светлая" : "Тёмная";
  }

  // ── Избранное ───────────────────────────────────────────────────────
  // Состояние избранного хранится на сервере (привязано к аккаунту) и
  // рендерится в DOM как data-favorited="1/0". JS только фильтрует строки.

  function applyFavoritesFilter(mode) {
    const rows = Array.from(document.querySelectorAll("[data-tournament-row]"));
    if (!rows.length) return;

    let visible = 0;
    rows.forEach((row) => {
      const favorited = row.getAttribute("data-favorited") === "1";
      const hide = mode === "only" && !favorited;
      row.classList.toggle("is-hidden-by-favorite", hide);
      if (!hide) visible += 1;
    });

    const empty = document.querySelector("[data-favorites-empty]");
    if (empty) empty.classList.toggle("d-none", !(mode === "only" && visible === 0));
  }

  function initFavorites() {
    document.addEventListener("click", (e) => {
      const button = e.target.closest("[data-show-favorites]");
      if (button) {
        document.querySelectorAll("[data-show-favorites]").forEach((b) => b.classList.remove("active"));
        button.classList.add("active");
        applyFavoritesFilter(button.getAttribute("data-show-favorites"));
      }
    });
  }

  function initCopyButtons() {
    document.addEventListener("click", async (e) => {
      const button = e.target.closest("[data-copy-current-url]");
      if (button) {
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
      }
    });
  }

  // --- Плавные анимации при скролле ---
  function initScrollAnimations(target) {
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

    const container = target || document;
    const animatedElements = container.querySelectorAll
      ? container.querySelectorAll('.card, .section-panel, .table-panel, .empty-state')
      : [];

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

  // Делегированный обработчик переключения темы
  document.addEventListener("click", (e) => {
    const btn = e.target.closest("#themeToggle");
    if (btn) {
      const root = document.documentElement;
      const current = root.getAttribute("data-theme") === "light" ? "light" : "dark";
      root.classList.add("theme-switching");
      applyTheme(current === "light" ? "dark" : "light");
      syncToggleUi();
      window.setTimeout(() => root.classList.remove("theme-switching"), 300);
    }
  });

  // Запуск инициализации при HTMX-загрузке/свапе
  document.addEventListener("htmx:load", function (evt) {
    const target = evt.detail.elt || document;
    initScrollAnimations(target);
    syncToggleUi();
  });

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

    // Запуск всех глобальных модулей
    initFavorites();
    initCopyButtons();
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
