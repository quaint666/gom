// Runs before the page is painted, so a chosen colour scheme never flashes the wrong one first.
// It is a separate file, not inline, because the page policy forbids inline script.
try {
  var stored = localStorage.getItem("color-scheme");
  if (stored === "light" || stored === "dark") {
    document.querySelector('meta[name="color-scheme"]').content = stored;
  }
} catch (e) { /* site data blocked: follow the system */ }
