# The Python sandbox (compose profile "sandbox"): Python 3.13 with data and
# science packages, the tools that convert what a model cannot (documents,
# images, audio and video, scans), and the runner that isolates each job
# (runner.py). Built once; at run time it has no network at all.
#
# Wheels only, versions pinned: nothing is compiled here. The one exception is
# odfpy (OpenDocument, for pandas and .ods files), published only as source: it
# is pure Python, so building it copies files and compiles nothing.
FROM python:3.13-slim-bookworm

# System tools, headless: LibreOffice (documents to PDF and between formats),
# pandoc (Markdown, HTML, docx, EPUB), ffmpeg (audio and video), ImageMagick,
# Tesseract OCR, poppler (PDF to text and pictures), Graphviz (diagrams), and
# fonts for many scripts (Noto: Latin, Cyrillic, Greek, Arabic and Persian...).
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      libreoffice-writer-nogui libreoffice-calc-nogui libreoffice-impress-nogui \
      pandoc ffmpeg imagemagick tesseract-ocr tesseract-ocr-eng poppler-utils graphviz \
      fonts-noto-core fonts-dejavu-core fontconfig \
 && rm -rf /var/lib/apt/lists/* \
 && fc-cache -f

RUN pip install --no-cache-dir --only-binary=:all: --no-binary=odfpy \
      numpy==2.5.3 pandas==3.0.6 matplotlib==3.11.2 pillow==12.3.0 \
      openpyxl==3.1.5 scipy==1.18.1 sympy==1.14.0 \
      duckdb==1.5.5 pyarrow==25.0.1 statsmodels==0.15.0 scikit-learn==1.9.1 seaborn==0.13.2 \
      python-docx==1.2.0 python-pptx==1.0.2 xlsxwriter==3.2.9 odfpy==1.4.1 reportlab==5.0.1 \
      pypdf==6.19.0 pdfplumber==0.11.10 lxml==6.1.3 beautifulsoup4==4.15.0 markdown==3.11 \
      pyyaml==6.0.3 jinja2==3.1.6 tabulate==0.10.0 chardet==7.6.0 rapidfuzz==3.14.6 networkx==3.7 \
      pint==0.26.1 holidays==0.105 tzdata==2026.4 numpy-financial==1.1.0 \
      qrcode==8.2 graphviz==0.21 pytesseract==0.3.13 \
      opencv-python-headless==5.0.0.93 scikit-image==0.26.0 imageio==2.37.4 tifffile==2026.9.20 \
      pywavelets==1.10.0 pillow-heif==1.8.0 \
      polars==1.44.2 plotly==7.1.0 xgboost-cpu==3.4.1 lightgbm==4.7.0 numba==0.67.0 llvmlite==0.49.0 \
      geopandas==1.1.4 shapely==2.1.2 pyproj==3.8.0 pyogrio==0.13.0 \
 # Matplotlib's font cache, built now: at run time its directory is read only.
 && MPLCONFIGDIR=/opt/mplconfig python -c "import matplotlib.pyplot" \
 && chmod -R a+rX /opt/mplconfig \
 # No set-user-ID programs to climb back up with (no-new-privileges stops them too).
 && find / -xdev -perm /6000 -type f -exec chmod a-s {} + \
 && rm -rf /root/.cache

# More languages: OCR data for Arabic, Persian and Chinese (simplified and
# traditional), and the Noto CJK fonts, so documents and charts in Chinese,
# Japanese and Korean show their letters. A layer of its own: the ones above stay
# as they were built. Matplotlib's font cache again, to find the new fonts.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      tesseract-ocr-ara tesseract-ocr-fas tesseract-ocr-chi-sim tesseract-ocr-chi-tra fonts-noto-cjk \
 && rm -rf /var/lib/apt/lists/* \
 && fc-cache -f \
 && rm -rf /opt/mplconfig && MPLCONFIGDIR=/opt/mplconfig python -c "import matplotlib.pyplot" \
 && chmod -R a+rX /opt/mplconfig \
 && find / -xdev -perm /6000 -type f -exec chmod a-s {} +

# LibreOffice as a job can run it (soffice and libreoffice): see the script.
COPY --chmod=755 soffice /usr/local/bin/soffice
RUN ln -s soffice /usr/local/bin/libreoffice

# The tools the chat calls by name (Math, Date and time, Data, Convert,
# Diagrams): Python functions run here, like any other job.
COPY sandbox_tools /opt/toolbox/sandbox_tools
RUN echo /opt/toolbox > "$(python -c 'import site; print(site.getsitepackages()[0])')/sandbox-tools.pth" \
 && python -c "import sandbox_tools"

COPY runner.py /runner.py
ENTRYPOINT ["python", "-I", "-u", "/runner.py"]
