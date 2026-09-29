// Sets the theme before the first paint (kept out of index.html so a strict script-src 'self' policy still
// allows it): the remembered choice (mq.theme), else the OS. There is one density (src/design/density.ts).
(function () {
  var choice = "system";
  try {
    choice = localStorage.getItem("mq.theme") || "system";
    localStorage.removeItem("mq.density");
  } catch (e) {
    /* storage blocked: defaults */
  }
  var dark = choice === "dark" || (choice !== "light" && !!window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.dataset.theme = dark ? "dark" : "light";
})();
