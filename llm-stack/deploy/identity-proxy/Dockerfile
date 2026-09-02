# syntax=docker/dockerfile:1
FROM python:3.13-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1

# Pinned: this sits in the request path of every chat message, and an
# unpinned dependency there turns an unrelated upstream release into a
# gateway outage.
RUN pip install "starlette==0.42.0" "httpx==0.28.1" "uvicorn==0.34.0"

WORKDIR /app
COPY app.py .

# Not 'proxy': Debian slim already ships a system account by that name and
# useradd exits 9 rather than reusing it.
RUN useradd --create-home --shell /usr/sbin/nologin idproxy
USER idproxy

EXPOSE 8080
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080", \
     "--no-access-log"]
