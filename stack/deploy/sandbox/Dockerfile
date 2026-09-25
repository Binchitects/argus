# The Python sandbox (compose profile "sandbox"): Python 3.13 with data and
# science packages, and the runner that isolates each job (runner.py).
#
# Wheels only: nothing is compiled here. The versions are pinned; the image is
# built once and then runs with no network at all.
FROM python:3.13-slim-bookworm

RUN pip install --no-cache-dir --only-binary=:all: \
      numpy==2.5.3 pandas==3.0.6 matplotlib==3.11.2 pillow==12.3.0 \
      openpyxl==3.1.5 scipy==1.18.1 sympy==1.14.0 \
 # Matplotlib's font cache, built now: at run time its directory is read only.
 && MPLCONFIGDIR=/opt/mplconfig python -c "import matplotlib.pyplot" \
 && chmod -R a+rX /opt/mplconfig \
 # No set-user-ID programs to climb back up with (no-new-privileges stops them too).
 && find / -xdev -perm /6000 -type f -exec chmod a-s {} + \
 && rm -rf /root/.cache

COPY runner.py /runner.py
ENTRYPOINT ["python", "-I", "-u", "/runner.py"]
