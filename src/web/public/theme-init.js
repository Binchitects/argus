// The saved theme ("light", "dark" or "system"), colour theme and content width, applied before React loads so a
// dark-mode reader never sees a white flash. Storage can be unavailable: then "system", Arena's colours and wide.
// Also before the page's stylesheet: one the browser failed to fetch is fetched again.
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
  // A stylesheet the browser failed to fetch (the connection dropped, or Chrome reloaded its certificates and failed
  // every request in flight) is fetched once more: else the page stays unstyled until a reload. Its scripts need no
  // such help: a module is fetched again as it is imported. A load error does not bubble: it is caught on its way down.
  document.addEventListener(
    'error',
    function (e) {
      var link = e.target
      if (!(link instanceof HTMLLinkElement) || link.rel !== 'stylesheet' || link.dataset.again) return
      var again = link.cloneNode()
      again.dataset.again = '1'
      link.replaceWith(again)
    },
    true,
  )
})()
