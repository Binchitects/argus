# syntax=docker/dockerfile:1
FROM python:3.13-slim-bookworm AS base

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

# ---------------------------------------------------------------- test ------
# The Indexing card is built by string concatenation with closures in it, which
# is how its log block came to render the repository list instead of the log
# while still looking plausible. test_app.py is plain asserts; see its
# docstring for why there is no pytest here.
FROM base AS test
COPY test_app.py .
# The receipt is what makes this stage part of the build graph. BuildKit builds
# only what the target depends on, so without something in `final` COPYing from
# here, `--target final` would skip the checks entirely -- the same trap the
# argus Dockerfile documents.
RUN python test_app.py && echo "rendering checks: passed" > /app/.checked

# --------------------------------------------------------------- runtime -----
FROM base AS final
COPY --from=test /app/.checked /app/.checked
RUN rm /app/.checked

RUN useradd --create-home --shell /usr/sbin/nologin panel
# No USER: entrypoint.sh drops to the owner of the mounted config/authelia,
# so the panel can read and replace users.yml whatever the host uid is.
COPY --chmod=755 entrypoint.sh /usr/local/bin/entrypoint.sh
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]

EXPOSE 8080
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080", \
     "--no-access-log", "--proxy-headers"]
