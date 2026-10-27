// The weak code each rule in javascript.yaml must find (ruleid) and the fixed code it must not (ok).
const childProcess = require("child_process");
const { exec } = require("child_process");
const fs = require("fs");
const path = require("path");
const axios = require("axios");
const jwt = require("jsonwebtoken");
const cors = require("cors");
const serialize = require("node-serialize");
const vm = require("vm");

async function sql(db, name, id) {
  // ruleid: js-sql-built-from-strings
  await db.query(`SELECT * FROM users WHERE name = '${name}'`);
  // ruleid: js-sql-built-from-strings
  await db.query("SELECT * FROM users WHERE id = " + id);
  const q = `DELETE FROM users WHERE id = ${id}`;
  // ruleid: js-sql-built-from-strings
  await db.execute(q);
  // ok: js-sql-built-from-strings
  await db.query("SELECT * FROM users WHERE name = $1", [name]);
  // ok: js-sql-built-from-strings
  await db.query("SELECT 1");
}

function commands(host) {
  // ruleid: js-command-from-strings
  childProcess.exec(`ping -c 1 ${host}`);
  // ruleid: js-command-from-strings
  exec("ping -c 1 " + host);
  // ok: js-command-from-strings
  childProcess.execFile("ping", ["-c", "1", host]);
  // ok: js-command-from-strings
  childProcess.exec("uptime");
}

function code(expr) {
  // ruleid: js-code-from-data
  eval(expr);
  // ruleid: js-code-from-data
  const f = new Function("a", expr);
  // ruleid: js-code-from-data
  vm.runInNewContext(expr);
  // ruleid: js-code-from-data
  setTimeout("run(" + expr + ")", 10);
  // ok: js-code-from-data
  setTimeout(() => run(expr), 10);
  // ok: js-code-from-data
  eval("1 + 1");
  return f;
}

function html(el, comment) {
  // ruleid: js-html-not-escaped
  el.innerHTML = "<p>" + comment + "</p>";
  // ruleid: js-html-not-escaped
  document.write(comment);
  // ok: js-html-not-escaped
  el.innerHTML = DOMPurify.sanitize(comment);
  // ok: js-html-not-escaped
  el.innerHTML = "<br>";
  // ok: js-html-not-escaped
  el.textContent = comment;
}

function Comment({ text }) {
  // ruleid: js-react-html-not-escaped
  const a = <div dangerouslySetInnerHTML={{ __html: text }} />;
  // ok: js-react-html-not-escaped
  const b = <div dangerouslySetInnerHTML={{ __html: DOMPurify.sanitize(text) }} />;
  // ok: js-react-html-not-escaped
  const c = <div>{text}</div>;
  return [a, b, c];
}

function tls() {
  // ruleid: js-tls-not-verified
  const agent = new https.Agent({ rejectUnauthorized: false });
  // ruleid: js-tls-not-verified
  process.env.NODE_TLS_REJECT_UNAUTHORIZED = "0";
  // ok: js-tls-not-verified
  const fine = new https.Agent({ ca: fs.readFileSync("ca.pem") });
  return [agent, fine];
}

app.get("/user", async (req, res) => {
  const name = req.query.name;
  // ruleid: js-sql-from-request, js-sql-built-from-strings
  const rows = await db.query("SELECT * FROM users WHERE name = '" + name + "'");
  // ok: js-sql-from-request
  const fine = await db.query("SELECT * FROM users WHERE name = $1", [name]);
  res.json([rows, fine]);
});

app.get("/ping", (req, res) => {
  // ruleid: js-command-from-request
  childProcess.execSync(req.query.cmd);
  // ok: js-command-from-request
  childProcess.execSync("uptime");
  res.end();
});

app.get("/file", (req, res) => {
  // ruleid: js-path-from-request
  fs.readFile(path.join(__dirname, "files", req.query.name), (e, d) => res.send(d));
});

app.get("/file2", (req, res) => {
  // ok: js-path-from-request
  res.sendFile(path.join(__dirname, "files", path.basename(req.query.name)));
});

app.get("/fetch", async (req, res) => {
  // ruleid: js-request-to-url-from-request
  const r = await axios.get(req.query.url);
  // ok: js-request-to-url-from-request
  const s = await axios.get("https://example.com/status");
  res.send([r.data, s.data]);
});

app.get("/go", (req, res) => {
  // ruleid: js-redirect-from-request
  res.redirect(req.query.next);
});

app.get("/go2", (req, res) => {
  // ok: js-redirect-from-request
  res.redirect("/home");
});

function secrets() {
  // ruleid: js-predictable-secret
  const resetToken = Math.random().toString(36).slice(2);
  // ok: js-predictable-secret
  const apiKey = require("crypto").randomBytes(32).toString("hex");
  // ok: js-predictable-secret
  const jitter = Math.random() * 100;
  return [resetToken, apiKey, jitter];
}

function tokens(token, key) {
  // ruleid: js-jwt-not-verified
  jwt.verify(token, key, { algorithms: ["none"] });
  // ok: js-jwt-not-verified
  jwt.verify(token, key, { algorithms: ["RS256"] });
}

// ruleid: js-cors-any-origin-with-credentials
app.use(cors({ origin: true, credentials: true }));
// ok: js-cors-any-origin-with-credentials
app.use(cors({ origin: ["https://app.example.com"], credentials: true }));

function deserialise(cookie) {
  // ruleid: js-unsafe-deserialization
  return serialize.unserialize(cookie);
}

function deserialise2(cookie) {
  // ok: js-unsafe-deserialization
  return JSON.parse(cookie);
}
