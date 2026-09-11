# syntax=docker/dockerfile:1
FROM python:3.13-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1

# Pinned for the same reason as identity-proxy: this holds the LiteLLM master
# key and writes Authelia's user database, so an unpinned dependency turns an
# unrelated upstream release into an authentication outage.
#
# argon2-cffi must agree with Authelia on parameters (m=65536,t=3,p=4). A hash
# written with different ones is accepted here and rejected at login, which is
# a failure nobody sees until a person cannot sign in.
RUN pip install \
      "starlette==0.42.0" \
      "uvicorn==0.34.0" \
      "python-multipart==0.0.20" \
      "PyYAML==6.0.2" \
      "argon2-cffi==23.1.0"

WORKDIR /app
COPY app.py .

RUN useradd --create-home --shell /usr/sbin/nologin panel
USER panel

EXPOSE 8080
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080", \
     "--no-access-log", "--proxy-headers"]
