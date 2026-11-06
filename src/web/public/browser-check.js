// Code Arena's IDE is built for Chrome and Edge 109, Firefox 115 and Safari 16 or newer (vite.code-arena.config.ts). An
// older browser fails on the page's modules without a word, or draws it without its layout: this says so instead. A plain
// script with nothing newer than the browsers it looks for, before the page's modules. It checks what the page cannot do
// without: dvh and container queries in CSS, structuredClone and Array.prototype.findLast in JavaScript.
;(function () {
  var css = window.CSS && typeof window.CSS.supports === 'function'
  var ok =
    css &&
    window.CSS.supports('height', '1dvh') &&
    window.CSS.supports('container-type', 'inline-size') &&
    typeof window.structuredClone === 'function' &&
    typeof Array.prototype.findLast === 'function'
  if (ok) return
  function say() {
    var root = document.getElementById('root')
    if (!root) return
    var box = document.createElement('div')
    box.setAttribute('role', 'alert')
    box.style.maxWidth = '36rem'
    box.style.margin = '4rem auto'
    box.style.padding = '0 1rem'
    box.style.font = '16px/1.5 system-ui, sans-serif'
    var title = document.createElement('h1')
    title.style.fontSize = '20px'
    title.textContent = 'This browser is too old for Code Arena'
    var text = document.createElement('p')
    text.textContent =
      'The IDE needs Chrome or Edge 109 or newer, Firefox 115 or newer, or Safari 16 or newer. Open the address in a newer browser, or set CODE_ARENA_BROWSER to one before you start code-arena. code-arena chat runs the agent in a terminal meanwhile.'
    box.appendChild(title)
    box.appendChild(text)
    root.parentNode.insertBefore(box, root)
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', say)
  else say()
})()
