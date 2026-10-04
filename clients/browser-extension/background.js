// The context menu: "Ask Argus Arena about …" on a selection or a page opens the app's
// /ask with it. Chrome runs this as a service worker (lib.js imported here); Firefox
// as a background script, after lib.js (manifest.json lists both).
if (typeof importScripts === 'function' && typeof ArenaLib === 'undefined') importScripts('lib.js')

const ext = globalThis.browser ?? globalThis.chrome

ext.runtime.onInstalled.addListener(() => {
  ext.contextMenus.create({ id: 'ask-selection', title: 'Ask Argus Arena about “%s”', contexts: ['selection'] })
  ext.contextMenus.create({ id: 'ask-page', title: 'Ask Argus Arena about this page', contexts: ['page'] })
})

/** The page's title, address and text (or the selection), read in the tab (the click allowed it: activeTab). */
async function readTab(tab) {
  const [result] = await ext.scripting.executeScript({
    target: { tabId: tab.id },
    func: () => ({ title: document.title, url: location.href, selection: String(getSelection() || ''), text: document.body ? document.body.innerText : '' }),
  })
  return result.result
}

ext.contextMenus.onClicked.addListener(async (info, tab) => {
  const { app } = await ext.storage.local.get('app')
  if (!ArenaLib.appOrigin(app)) {
    await ext.runtime.openOptionsPage()
    return
  }
  let quote
  if (info.menuItemId === 'ask-selection') {
    // The menu's own text is the selection without its line breaks: the tab's has them, when it can be read.
    const page = await readTab(tab).catch(() => null)
    quote = { title: tab.title, url: info.pageUrl || tab.url, text: page && page.selection ? page.selection : info.selectionText, selection: true }
  } else {
    const page = await readTab(tab)
    quote = { title: page.title || tab.title, url: page.url, text: page.text, selection: false }
  }
  await ext.tabs.create({ url: ArenaLib.askUrl(app, quote), index: tab.index + 1 })
})
