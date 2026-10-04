// The options: the app's address, and for asking in the popup the API key, a model and the gateway.
const ext = globalThis.browser ?? globalThis.chrome
const fields = ['app', 'apiKey', 'model', 'gateway']
const $ = (id) => document.getElementById(id)

ext.storage.local.get(fields).then((saved) => {
  for (const f of fields) $(f).value = saved[f] || ''
})

$('options').addEventListener('submit', async (e) => {
  e.preventDefault()
  const app = ArenaLib.appOrigin($('app').value)
  if (!app) {
    $('saved').textContent = 'The app’s address is an https:// address, e.g. https://llm.example.com.'
    return
  }
  const apiKey = $('apiKey').value.trim()
  const gateway = apiKey ? ArenaLib.gatewayOrigin(app, $('gateway').value) : null
  // Asking in the popup reaches the gateway: the browser asks the person to allow that address once.
  if (gateway && !(await ext.permissions.request({ origins: [`${gateway}/*`] }))) {
    $('saved').textContent = 'Without access to the gateway, asking in the popup cannot work. Handing pages to the app still does.'
  }
  await ext.storage.local.set({ app, apiKey, model: $('model').value.trim(), gateway: $('gateway').value.trim() })
  $('app').value = app
  $('saved').textContent ||= 'Saved.'
  setTimeout(() => ($('saved').textContent = ''), 4000)
})
