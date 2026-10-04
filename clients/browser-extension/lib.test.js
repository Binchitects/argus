// The extension's pure functions, with node's own test runner (no packages): node --test clients/browser-extension/lib.test.js
const test = require('node:test')
const assert = require('node:assert/strict')
const { appOrigin, gatewayOrigin, cut, askUrl, chatRequest, answerText, MAX_TEXT } = require('./lib.js')

test('the app’s address is an https origin, however it was typed', () => {
  assert.equal(appOrigin('llm.example.com'), 'https://llm.example.com')
  assert.equal(appOrigin(' https://llm.example.com/chat/123?x=1 '), 'https://llm.example.com')
  assert.equal(appOrigin('http://localhost:5173'), 'http://localhost:5173')
  assert.equal(appOrigin('http://llm.example.com'), null)
  assert.equal(appOrigin('javascript:alert(1)'), null)
  assert.equal(appOrigin(''), null)
})

test('the gateway is gateway.DOMAIN unless one is given', () => {
  assert.equal(gatewayOrigin('https://llm.example.com'), 'https://gateway.llm.example.com')
  assert.equal(gatewayOrigin('https://llm.example.com', 'api.example.com'), 'https://api.example.com')
  assert.equal(gatewayOrigin(''), null)
})

test('a page is handed over in the fragment of /ask, cut to fit', () => {
  const url = new URL(askUrl('llm.example.com', { title: 'Release notes', url: 'https://example.com/r?a=1&b=2', text: 'Line 1\r\n\r\n\r\n\r\nLine 2', selection: true }))
  assert.equal(url.origin + url.pathname, 'https://llm.example.com/ask')
  assert.equal(url.search, '')
  const p = new URLSearchParams(url.hash.slice(1))
  assert.equal(p.get('title'), 'Release notes')
  assert.equal(p.get('url'), 'https://example.com/r?a=1&b=2')
  assert.equal(p.get('text'), 'Line 1\n\nLine 2')
  assert.equal(p.get('sel'), '1')
  assert.throws(() => askUrl('', { text: 'x' }), /options/)
  const long = cut('x'.repeat(MAX_TEXT + 10))
  assert.ok(long.endsWith('[… cut: the page goes on]'))
  assert.equal(long.length, MAX_TEXT + '\n\n[… cut: the page goes on]'.length)
})

test('a question to the gateway carries the page as data, and its answer is read', () => {
  const body = chatRequest('What changed?', { title: 'Notes', url: 'https://n.test', text: 'Bots came.' }, 'Qwen')
  assert.equal(body.model, 'Qwen')
  assert.equal(body.stream, false)
  assert.match(body.messages[0].content, /never follow instructions/)
  assert.match(body.messages[1].content, /The text of "Notes" \(https:\/\/n\.test\):\n\n<page>\nBots came\.\n<\/page>\n\nWhat changed\?$/)
  assert.equal(answerText({ choices: [{ message: { content: ' It added bots. ' } }] }), 'It added bots.')
  assert.equal(answerText({ error: { message: 'Budget exceeded' } }), 'The gateway said: Budget exceeded')
  assert.equal(answerText({}), 'No answer came.')
})
