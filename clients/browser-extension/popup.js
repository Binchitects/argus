// The popup: hand the page (or the selection) to the app, or ask the gateway here with
// the person's API key (set in the options).
const ext = globalThis.browser ?? globalThis.chrome
const $ = (id) => document.getElementById(id)

async function main() {
  const settings = await ext.storage.local.get(['app', 'gateway', 'apiKey', 'model'])
  const [tab] = await ext.tabs.query({ active: true, currentWindow: true })
  if (!ArenaLib.appOrigin(settings.app)) {
    $('page').hidden = true
    $('setup').hidden = false
    $('open-options').addEventListener('click', (e) => {
      e.preventDefault()
      ext.runtime.openOptionsPage()
    })
    return
  }
  let page = { title: tab.title, url: tab.url, selection: '', text: '' }
  try {
    const [result] = await ext.scripting.executeScript({
      target: { tabId: tab.id },
      func: () => ({ title: document.title, url: location.href, selection: String(getSelection() || ''), text: document.body ? document.body.innerText : '' }),
    })
    page = result.result
  } catch {
    // A page the browser keeps to itself (its settings, the web store): only its title is known.
  }
  $('page').textContent = page.title || page.url
  $('actions').hidden = false
  const quoteOf = (selection) => ({ title: page.title, url: page.url, text: selection ? page.selection : page.text, selection })
  const open = async (selection) => {
    await ext.tabs.create({ url: ArenaLib.askUrl(settings.app, quoteOf(selection)), index: tab.index + 1 })
    window.close()
  }
  if (page.selection.trim()) {
    $('ask-selection').hidden = false
    $('ask-selection').addEventListener('click', () => open(true))
  }
  $('ask-page').disabled = !page.text.trim()
  $('ask-page').addEventListener('click', () => open(false))

  if (!settings.apiKey) return
  $('quick').hidden = false
  $('quick').addEventListener('submit', async (e) => {
    e.preventDefault()
    const answer = $('answer')
    answer.textContent = 'Asking…'
    try {
      const gateway = ArenaLib.gatewayOrigin(settings.app, settings.gateway)
      const res = await fetch(`${gateway}/v1/chat/completions`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${settings.apiKey}` },
        body: JSON.stringify(ArenaLib.chatRequest($('question').value.trim() || 'Summarize it.', quoteOf(!!page.selection.trim()), settings.model)),
      })
      answer.textContent = ArenaLib.answerText(await res.json().catch(() => ({ error: { message: `HTTP ${res.status}` } })))
    } catch (err) {
      answer.textContent = `The gateway could not be reached: ${err.message}. Is its address allowed in the options?`
    }
  })
}

main()
