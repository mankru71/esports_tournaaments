// Theme manager (JS-first): switches theme reliably by setting CSS variables (custom + Bootstrap vars)
// No Bootstrap theme plugin required.
const THEME_KEY = 'theme';

const PALETTES = {
  dark: {
    bg: '#090c1a',
    surface: '#141b30',
    surface2: '#1b2540',
    text: '#e9edff',
    muted: '#a6b3d1',
    border: 'rgba(160, 177, 218, 0.25)',
    accent: '#4cc9f0',
    accent2: '#6e8bff',
    danger: '#ff6c8f',
    success: '#31d0aa',
    warning: '#f7b955',
    shadow: '0 10px 28px rgba(0, 0, 0, 0.3)',
    bgGradient: 'radial-gradient(circle at top right, #16233f, #090c1a 40%)',
  },
  light: {
    bg: '#eff3fb',
    surface: '#ffffff',
    surface2: '#f4f7ff',
    text: '#1b2440',
    muted: '#5f6d91',
    border: 'rgba(95, 109, 145, 0.28)',
    accent: '#0f6ef7',
    accent2: '#5f32ff',
    danger: '#d93256',
    success: '#0b9874',
    warning: '#b97400',
    shadow: '0 8px 24px rgba(22, 28, 45, 0.10)',
    bgGradient: 'linear-gradient(180deg, #f8faff 0%, #eff3fb 100%)',
  },
};

function getSavedTheme() {
  try { return localStorage.getItem(THEME_KEY); } catch (_) { return null; }
}
function saveTheme(theme) {
  try { localStorage.setItem(THEME_KEY, theme); } catch (_) {}
}

function setVar(name, value) {
  document.documentElement.style.setProperty(name, value);
}

// We set BOTH our custom variables and key Bootstrap CSS variables so Bootstrap components
// (tables, buttons, forms) also visibly switch.
function applyTheme(theme) {
  const t = theme === 'light' ? 'light' : 'dark';
  const p = PALETTES[t];
  const root = document.documentElement;

  // Mark theme
  root.dataset.theme = t;
  root.classList.toggle('theme-dark', t === 'dark');
  root.classList.toggle('theme-light', t === 'light');
  root.style.colorScheme = t === 'dark' ? 'dark' : 'light';

  // Custom vars (used by app.css)
  setVar('--bg', p.bg);
  setVar('--surface', p.surface);
  setVar('--surface-2', p.surface2);
  setVar('--text', p.text);
  setVar('--muted', p.muted);
  setVar('--border', p.border);
  setVar('--accent', p.accent);
  setVar('--accent-2', p.accent2);
  setVar('--danger', p.danger);
  setVar('--success', p.success);
  setVar('--warning', p.warning);
  setVar('--shadow', p.shadow);
  setVar('--bg-gradient', p.bgGradient);

  // Bootstrap vars (so .btn, .table, .form-control and other components react)
  setVar('--bs-body-bg', p.bg);
  setVar('--bs-body-color', p.text);
  setVar('--bs-secondary-color', p.muted);
  setVar('--bs-tertiary-bg', p.surface);
  setVar('--bs-border-color', p.border);
  setVar('--bs-link-color', p.accent);
  setVar('--bs-link-hover-color', p.accent2);

  // Controls background
  setVar('--bs-emphasis-color', p.text);

  // Save
  saveTheme(t);

  // Update UI label/icon
  const label = document.getElementById('themeToggleLabel');
  const icon = document.getElementById('themeToggleIcon');
  if (label) label.textContent = t === 'dark' ? 'Тёмная' : 'Светлая';
  if (icon) icon.textContent = t === 'dark' ? '🌙' : '☀️';

  // Meta theme-color
  const metaTheme = document.getElementById('meta-theme-color');
  if (metaTheme) metaTheme.setAttribute('content', p.bg);
}

function initTheme() {
  const initial = getSavedTheme() || 'dark';
  applyTheme(initial);

  const btn = document.getElementById('themeToggle');
  if (!btn) return;
  btn.addEventListener('click', () => {
    const current = document.documentElement.dataset.theme || 'dark';
    applyTheme(current === 'dark' ? 'light' : 'dark');
  });
}

document.addEventListener('DOMContentLoaded', () => {
  initTheme();

  // auto-dismiss alerts
  document.querySelectorAll('[data-auto-dismiss="true"]').forEach((el) => {
    setTimeout(() => {
      el.classList.add('fade');
      el.classList.remove('show');
      setTimeout(() => el.remove(), 250);
    }, 3500);
  });

  // footer year
  const yearNode = document.getElementById('footer-year');
  if (yearNode) yearNode.textContent = String(new Date().getFullYear());
});
