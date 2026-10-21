# The browser agent's browser (compose profile "browser"): Debian's Chromium, headless, driven by the app over the
# DevTools protocol (Chat/Tools/Browser). It reaches nothing by itself: its network is internal, and every page goes
# through the app's proxy, which allows public sites only (and an admin's lists). Its root is read-only and it runs as
# its own user; each chat gets its own browser context (cookies and storage apart), closed when the chat is done with it.
FROM python:3.13-slim-bookworm

RUN apt-get update \
 && apt-get install -y --no-install-recommends chromium fonts-noto-core fonts-liberation \
 && rm -rf /var/lib/apt/lists/* \
 && find / -xdev -perm /6000 -type f -exec chmod a-s {} + \
 && useradd --uid 10001 --create-home --home-dir /home/browser browser

COPY --chmod=755 run.py /run.py
USER 10001
EXPOSE 9222
ENTRYPOINT ["python", "-I", "-u", "/run.py"]
