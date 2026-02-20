const THEME_KEY = 'theme';

function applyTheme(theme) {
  const safeTheme = theme === 'light' ? 'light' : 'dark';
  document.documentElement.setAttribute('data-theme', safeTheme);
  localStorage.setItem(THEME_KEY, safeTheme);

  const toggleLabel = document.getElementById('themeToggleLabel');
  const toggleIcon = document.getElementById('themeToggleIcon');
  if (toggleLabel) {
    toggleLabel.textContent = safeTheme === 'dark' ? 'Тёмная' : 'Светлая';
  }
  if (toggleIcon) {
    toggleIcon.textContent = safeTheme === 'dark' ? '🌙' : '☀️';
  }
}

function initTheme() {
  const savedTheme = localStorage.getItem(THEME_KEY) || 'dark';
  applyTheme(savedTheme);

  const toggle = document.getElementById('themeToggle');
  if (toggle) {
    toggle.addEventListener('click', () => {
      const currentTheme = document.documentElement.getAttribute('data-theme') || 'dark';
      applyTheme(currentTheme === 'dark' ? 'light' : 'dark');
    });
  }
}

document.addEventListener('DOMContentLoaded', () => {
  initTheme();

  document.querySelectorAll('[data-auto-dismiss="true"]').forEach((el) => {
    setTimeout(() => {
      el.classList.add('fade');
      el.classList.remove('show');
      setTimeout(() => el.remove(), 250);
    }, 3500);
  });

  const yearNode = document.getElementById('footer-year');
  if (yearNode) {
    yearNode.textContent = String(new Date().getFullYear());
  }
});
