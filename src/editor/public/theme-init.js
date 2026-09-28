// Sets the theme and density before the first paint (kept out of index.html so a strict
// script-src 'self' policy still allows it): the remembered choice (mq.theme), else the OS.
(function () {
  var choice = "system";
  var density = "compact";
  try {
    choice = localStorage.getItem("mq.theme") || "system";
    density = localStorage.getItem("mq.density") === "comfortable" ? "comfortable" : "compact";
  } catch (e) {
    /* storage blocked: defaults */
  }
  var dark = choice === "dark" || (choice !== "light" && !!window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.dataset.theme = dark ? "dark" : "light";
  document.documentElement.dataset.density = density;
})();
