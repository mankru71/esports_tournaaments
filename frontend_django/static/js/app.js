document.addEventListener('DOMContentLoaded', () => {
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
