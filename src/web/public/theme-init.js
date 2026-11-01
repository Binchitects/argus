// The saved theme ("light", "dark" or "system"), colour theme and content width, applied before React loads so a
// dark-mode reader never sees a white flash. Storage can be unavailable: then "system", Arena's colours and wide.
;(function () {
  var pref = 'system'
  try {
    pref = localStorage.getItem('theme') || 'system'
  } catch {
    // storage unavailable (private window): follow the system
  }
  var dark = pref === 'dark' || (pref === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches)
  document.documentElement.classList.toggle('dark', dark)
  document.documentElement.style.colorScheme = dark ? 'dark' : 'light'
  // The colour theme (sakura, midnight...); the default has none.
  try {
    var colors = localStorage.getItem('color-theme')
    if (colors && colors !== 'default') document.documentElement.dataset.colorTheme = colors
  } catch {
    // storage unavailable: Arena's colours
  }
  // The content width ("comfortable", "wide" or "full"); CSS treats a missing one as wide.
  try {
    var width = localStorage.getItem('width')
    if (width) document.documentElement.dataset.width = width
  } catch {
    // storage unavailable: wide
  }
})()
