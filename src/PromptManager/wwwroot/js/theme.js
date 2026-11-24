// Runs in <head>, before the page paints, so a reader who picked a theme
// does not see the other one flash first. "auto" follows the system.
(function () {
    try {
        var theme = localStorage.getItem("pm-theme");
        if (theme === "light" || theme === "dark") document.documentElement.setAttribute("data-theme", theme);
    } catch (e) { /* storage blocked: follow the system */ }
})();
