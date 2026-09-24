// The saved theme ("light", "dark" or "system"), applied before React loads so a
// dark-mode reader never sees a white flash. Storage can be unavailable: then "system".
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
})()
