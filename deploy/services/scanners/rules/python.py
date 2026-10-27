# The weak code each rule in python.yaml must find (ruleid) and the fixed code it must not (ok).
import hashlib
import os
import pickle
import random
import secrets
import ssl
import subprocess
import tempfile

import jwt
import requests
import torch
import yaml
from django.utils.safestring import mark_safe
from django.views.decorators.csrf import csrf_exempt
from flask import Flask, redirect, render_template_string, request, send_file, url_for
from werkzeug.utils import secure_filename

app = Flask(__name__)


def sql(cursor, name, user_id):
    # ruleid: python-sql-built-from-strings
    cursor.execute(f"SELECT * FROM users WHERE name = '{name}'")
    # ruleid: python-sql-built-from-strings
    cursor.execute("SELECT * FROM users WHERE id = %s" % user_id)
    # ruleid: python-sql-built-from-strings
    cursor.execute("SELECT * FROM users WHERE name = '" + name + "'")
    q = "DELETE FROM users WHERE id = {}".format(user_id)
    # ruleid: python-sql-built-from-strings
    cursor.execute(q)
    # ok: python-sql-built-from-strings
    cursor.execute("SELECT * FROM users WHERE name = %s", (name,))
    # ok: python-sql-built-from-strings
    cursor.execute("SELECT 1")


@app.route("/user")
def user(db):
    name = request.args.get("name")
    # ruleid: python-sql-from-request, python-sql-built-from-strings
    db.execute("SELECT * FROM users WHERE name = '" + name + "'")
    # ok: python-sql-from-request
    db.execute("SELECT * FROM users WHERE name = ?", (name,))


def commands(host):
    # ruleid: python-command-from-strings
    subprocess.run("ping -c 1 " + host, shell=True)
    # ruleid: python-command-from-strings
    os.system(f"ping -c 1 {host}")
    # ok: python-command-from-strings
    subprocess.run(["ping", "-c", "1", host])
    # ok: python-command-from-strings
    subprocess.run("ls -l", shell=True)
    # ok: python-command-from-strings
    os.system("clear")


@app.route("/ping")
def ping():
    host = request.args["host"]
    # ruleid: python-command-from-request
    subprocess.check_output(["ping", "-c", "1", host])
    # ok: python-command-from-request
    subprocess.check_output(["uptime"])


def code(expr):
    # ruleid: python-code-from-data
    eval(expr)
    # ruleid: python-code-from-data
    exec(expr)
    # ok: python-code-from-data
    eval("1 + 1")


@app.route("/hello")
def hello():
    name = request.args.get("name", "")
    # ruleid: python-template-from-request
    return render_template_string("<p>Hello " + name + "</p>")


@app.route("/hello2")
def hello2():
    name = request.args.get("name", "")
    # ok: python-template-from-request
    return render_template_string("<p>Hello {{ name }}</p>", name=name)


def deserialise(blob, path, model):
    # ruleid: python-unsafe-deserialization
    pickle.loads(blob)
    # ruleid: python-unsafe-deserialization
    yaml.load(blob, Loader=yaml.Loader)
    # ruleid: python-unsafe-deserialization
    yaml.unsafe_load(blob)
    # ruleid: python-unsafe-deserialization
    torch.load(model)
    # ok: python-unsafe-deserialization
    yaml.safe_load(blob)
    # ok: python-unsafe-deserialization
    yaml.load(blob, Loader=yaml.SafeLoader)
    # ok: python-unsafe-deserialization
    torch.load(model, weights_only=True)


def tls(url):
    # ruleid: python-tls-not-verified
    requests.get(url, verify=False)
    # ruleid: python-tls-not-verified
    ctx = ssl._create_unverified_context()
    # ok: python-tls-not-verified
    requests.get(url, verify="/etc/ssl/ca.pem")
    # ok: python-tls-not-verified
    requests.get(url)
    return ctx


@app.route("/fetch")
def fetch():
    url = request.args.get("url")
    # ruleid: python-request-to-url-from-request
    return requests.get(url).text


@app.route("/fetch2")
def fetch2():
    # ok: python-request-to-url-from-request
    return requests.get("https://example.com/status").text


@app.route("/download")
def download():
    name = request.args.get("file")
    # ruleid: python-path-from-request
    return send_file(os.path.join("/srv/files", name))


@app.route("/download2")
def download2():
    name = secure_filename(request.args.get("file"))
    # ok: python-path-from-request
    return send_file(os.path.join("/srv/files", name))


@app.route("/go")
def go():
    # ruleid: python-redirect-from-request
    return redirect(request.args.get("next"))


@app.route("/go2")
def go2():
    # ok: python-redirect-from-request
    return redirect(url_for("home"))


def markup(comment):
    # ruleid: python-markup-not-escaped
    html = mark_safe("<p>" + comment + "</p>")
    # ok: python-markup-not-escaped
    html2 = mark_safe("<br>")
    return html, html2


def tokens(token, key):
    # ruleid: python-jwt-not-verified
    jwt.decode(token, options={"verify_signature": False})
    # ok: python-jwt-not-verified
    jwt.decode(token, key, algorithms=["RS256"])


def hashes(data):
    # ruleid: python-weak-hash
    hashlib.md5(data)
    # ok: python-weak-hash
    hashlib.md5(data, usedforsecurity=False)
    # ok: python-weak-hash
    hashlib.sha256(data)


def secrets_made():
    # ruleid: python-predictable-secret
    reset_token = random.randint(100000, 999999)
    # ok: python-predictable-secret
    api_key = secrets.token_urlsafe(32)
    # ok: python-predictable-secret
    delay = random.random()
    return reset_token, api_key, delay


def temp():
    # ruleid: python-insecure-temp-file
    name = tempfile.mktemp()
    # ok: python-insecure-temp-file
    f = tempfile.NamedTemporaryFile()
    return name, f


# ruleid: python-csrf-exempt
@csrf_exempt
def webhook(req):
    return None


def main():
    # ruleid: python-flask-debug
    app.run(debug=True)
    # ok: python-flask-debug
    app.run(debug=False)
