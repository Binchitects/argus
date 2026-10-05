# Laya, the decision model (server.py), on the CPU: the GPU is the chat model's.
# The weights are not in the image: the app fetches them into the model library
# (MODELS_DIR/laya/english and multilingual), and this reads them from there.
FROM python:3.13-slim

# PyTorch's CPU build (no CUDA libraries), then Laya and what it needs.
RUN pip install --no-cache-dir --extra-index-url https://download.pytorch.org/whl/cpu \
      torch==2.14.1+cpu laya==0.3.27

COPY server.py /app/server.py

ENV LAYA_DIR=/models/laya \
    LAYA_PORT=8000 \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1 \
    HF_HUB_DISABLE_TELEMETRY=1 \
    USE_TF=0 \
    HOME=/tmp \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

USER 65534:65534
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s \
  CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=4)"]
CMD ["python", "/app/server.py"]
