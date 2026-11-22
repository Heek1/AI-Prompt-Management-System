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
