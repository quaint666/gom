// Runs before the page is painted, so a chosen colour scheme never flashes the wrong one first.
// It is a separate file, not inline, because the page policy forbids inline script.
try {
  var stored = localStorage.getItem("color-scheme");
  if (stored === "light" || stored === "dark") {
    document.querySelector('meta[name="color-scheme"]').content = stored;
  }
  // The person's colour (Scripts/theme.ts): a code without its block in gom.css simply draws the default.
  var palette = localStorage.getItem("gom-palette");
  if (palette && palette !== "sage" && /^[a-z]{2,16}$/.test(palette)) document.documentElement.setAttribute("data-palette", palette);
} catch (e) { /* site data blocked: follow the system */ }
