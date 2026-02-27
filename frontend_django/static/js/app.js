// Глобальные мелкие UI-скрипты (без зависимости от Bootstrap theme).
// Цель: гарантированно работающий переключатель темы (dark/light)
// через установку CSS-переменных из JS.

(function () {
  "use strict";

  const THEME_KEY = "theme";

  // Палитры (CSS variables). Ключи ДОЛЖНЫ начинаться с "--".
  const PALETTES = {
    dark: {
      "--bg": "#090c1a",
      "--surface": "#101833",
      "--surface-2": "#121d3d",
      "--text": "#e8ecff",
      "--muted": "#9aa6d1",
      "--border": "rgba(255,255,255,0.10)",
      "--accent": "#4cc3ff",
      "--accent-2": "#9b7bff",
      "--accent-soft": "rgba(76,195,255,0.14)",
      "--accent-soft-strong": "rgba(76,195,255,0.22)",
      "--danger": "#ff4d6d",
      "--success": "#2de38a",
      "--warning": "#ffcc66",
      "--shadow": "0 12px 40px rgba(0,0,0,0.35)",
      "--bg-gradient": "radial-gradient(900px circle at 10% 8%, rgba(76,195,255,0.18), transparent 55%), radial-gradient(900px circle at 90% 15%, rgba(155,123,255,0.18), transparent 55%), linear-gradient(180deg, #070917 0%, var(--bg) 100%)",

      "--table-striped-bg": "rgba(255,255,255,0.04)",

      "--status-planned-bg": "rgba(154,166,209,0.14)",
      "--status-planned-border": "rgba(154,166,209,0.28)",
      "--status-planned-text": "#e8ecff",
      "--status-live-bg": "rgba(76,195,255,0.16)",
      "--status-live-border": "rgba(76,195,255,0.45)",
      "--status-live-text": "#e8ecff",
      "--status-finished-bg": "rgba(45,227,138,0.16)",
      "--status-finished-border": "rgba(45,227,138,0.42)",
      "--status-finished-text": "#e8ecff",
      "--status-approved-bg": "rgba(155,123,255,0.16)",
      "--status-approved-border": "rgba(155,123,255,0.42)",
      "--status-approved-text": "#e8ecff",
    },
    light: {
      "--bg": "#eff3fb",
      "--surface": "#ffffff",
      "--surface-2": "#f4f7ff",
      "--text": "#111827",
      "--muted": "#5f6d91",
      "--border": "rgba(17,24,39,0.12)",
      "--accent": "#0ea5e9",
      "--accent-2": "#6d28d9",
      "--accent-soft": "rgba(14,165,233,0.12)",
      "--accent-soft-strong": "rgba(14,165,233,0.18)",
      "--danger": "#e11d48",
      "--success": "#16a34a",
      "--warning": "#b45309",
      "--shadow": "0 16px 50px rgba(15, 23, 42, 0.12)",
      "--bg-gradient": "radial-gradient(900px circle at 10% 8%, rgba(14,165,233,0.18), transparent 55%), radial-gradient(900px circle at 90% 15%, rgba(109,40,217,0.14), transparent 55%), linear-gradient(180deg, #f8faff 0%, var(--bg) 100%)",

      "--table-striped-bg": "rgba(17,24,39,0.03)",

      "--status-planned-bg": "rgba(95,109,145,0.10)",
      "--status-planned-border": "rgba(95,109,145,0.26)",
      "--status-planned-text": "#111827",
      "--status-live-bg": "rgba(14,165,233,0.14)",
      "--status-live-border": "rgba(14,165,233,0.38)",
      "--status-live-text": "#111827",
      "--status-finished-bg": "rgba(22,163,74,0.14)",
      "--status-finished-border": "rgba(22,163,74,0.32)",
      "--status-finished-text": "#111827",
      "--status-approved-bg": "rgba(109,40,217,0.12)",
      "--status-approved-border": "rgba(109,40,217,0.32)",
      "--status-approved-text": "#111827",
    },
  };

  function safeGetTheme() {
    try {
      const v = localStorage.getItem(THEME_KEY);
      return v === "light" ? "light" : "dark";
    } catch {
      return "dark";
    }
  }

  function safeSetTheme(theme) {
    try {
      localStorage.setItem(THEME_KEY, theme);
    } catch {
      // ignore
    }
  }

  function setCssVars(palette) {
    const root = document.documentElement;
    Object.entries(palette).forEach(([key, value]) => {
      root.style.setProperty(key, value);
    });
  }

  function applyTheme(theme) {
    const normalized = theme === "light" ? "light" : "dark";
    document.documentElement.setAttribute("data-theme", normalized);
    // Bootstrap 5.3 color modes
    document.documentElement.setAttribute("data-bs-theme", normalized);
    setCssVars(PALETTES[normalized]);
    safeSetTheme(normalized);

    // Нативные элементы (скроллбар/формы в некоторых браузерах)
    try {
      document.documentElement.style.colorScheme = normalized === "dark" ? "dark" : "light";
    } catch {
      // ignore
    }

    // meta theme-color (чтобы мобилка/браузерная панель выглядела адекватно)
    const meta = document.getElementById("meta-theme-color");
    if (meta) meta.setAttribute("content", normalized === "light" ? "#eff3fb" : "#090c1a");
  }

  function syncToggleUi() {
    const theme = document.documentElement.getAttribute("data-theme") === "light" ? "light" : "dark";
    const icon = document.getElementById("themeToggleIcon");
    const label = document.getElementById("themeToggleLabel");
    if (icon) icon.textContent = theme === "light" ? "☀️" : "🌙";
    if (label) label.textContent = theme === "light" ? "Светлая" : "Тёмная";
  }

  // Применяем тему СРАЗУ при загрузке скрипта (чтобы не было «непонятно какой темы»).
  applyTheme(safeGetTheme());

  document.addEventListener("DOMContentLoaded", function () {
    // Автоскрытие алертов
    document.querySelectorAll("[data-auto-dismiss='true']").forEach((el) => {
      setTimeout(() => {
        try {
          el.classList.remove("show");
          el.classList.add("fade");
        } catch {
          // ignore
        }
      }, 4500);
    });

    // Текущий год в footer
    const yearEl = document.getElementById("footer-year");
    if (yearEl) yearEl.textContent = String(new Date().getFullYear());

    // Переключатель темы
    syncToggleUi();
    const btn = document.getElementById("themeToggle");
    if (btn) {
      btn.addEventListener("click", function () {
        const current = document.documentElement.getAttribute("data-theme") === "light" ? "light" : "dark";
        const next = current === "light" ? "dark" : "light";
        applyTheme(next);
        syncToggleUi();
      });
    }
  });
})();
