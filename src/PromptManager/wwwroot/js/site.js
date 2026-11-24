// Behaviour that used to live in inline onclick handlers and <script> blocks.
// The Content-Security-Policy allows scripts from this site only, so every
// page script is here and attached through data attributes.

// <button data-confirm="Really?"> asks before the click goes through.
document.addEventListener('click', function (event) {
    const button = event.target.closest('[data-confirm]');
    if (button && !window.confirm(button.getAttribute('data-confirm'))) {
        event.preventDefault();
    }
});

// Mobile: the sidebar's menu button opens and closes the navigation.
document.addEventListener('click', function (event) {
    const toggle = event.target.closest('[data-menu-toggle]');
    if (!toggle) return;
    const side = toggle.closest('[data-side]');
    const open = side.classList.toggle('is-open');
    toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
});

// <button data-copy-target="#id"> copies that element's text.
document.addEventListener('click', function (event) {
    const button = event.target.closest('[data-copy-target]');
    if (!button) return;

    const source = document.querySelector(button.getAttribute('data-copy-target'));
    if (!source || !navigator.clipboard) return;

    const original = button.innerHTML;
    navigator.clipboard.writeText(source.innerText).then(function () {
        button.innerHTML = '<i class="bi bi-check-lg"></i> Copied!';
        setTimeout(function () { button.innerHTML = original; }, 2000);
    });
});

// Theme switch: Auto follows the system; Light and Dark are remembered in
// this browser only (theme.js applies the choice before the page paints).
(function () {
    function current() {
        try { return localStorage.getItem('pm-theme') || 'auto'; } catch (e) { return 'auto'; }
    }
    function apply(theme) {
        if (theme === 'light' || theme === 'dark') document.documentElement.setAttribute('data-theme', theme);
        else document.documentElement.removeAttribute('data-theme');
        document.querySelectorAll('[data-theme-choice]').forEach(function (b) {
            b.setAttribute('aria-pressed', b.getAttribute('data-theme-choice') === theme ? 'true' : 'false');
        });
    }
    document.addEventListener('click', function (event) {
        const button = event.target.closest('[data-theme-choice]');
        if (!button) return;
        const theme = button.getAttribute('data-theme-choice');
        try {
            if (theme === 'auto') localStorage.removeItem('pm-theme');
            else localStorage.setItem('pm-theme', theme);
        } catch (e) { /* not remembered, still applied */ }
        apply(theme);
    });
    apply(current());
})();
