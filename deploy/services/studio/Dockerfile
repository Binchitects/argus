# The media studio's own models (server.py): on the GPU BiRefNet lite, Chatterbox, ACE-Step; on the CPU CLIPSeg.
# PyTorch from PyPI (its Linux wheels bring CUDA 13); the models' own pins (older torch and
# transformers) are not taken: they run on these. The weights are not in the image: the app
# fetches them into the model library (MODELS_DIR/studio), and this reads them from there.
FROM python:3.13-slim

RUN pip install --no-cache-dir torch==2.11.0 torchaudio==2.11.0 torchvision==0.26.0

# ACE-Step reads lyrics' languages with py3langid's 0.3 API.
RUN pip install --no-cache-dir \
      transformers==5.17.0 tokenizers==0.23.3 huggingface_hub==1.33.0 safetensors==0.8.0 sentencepiece==0.2.2 protobuf==6.33.6 \
      numpy==2.2.6 pillow==13.0.0 einops==0.8.2 fastapi==0.136.3 uvicorn==0.54.0 \
      timm==1.0.30 kornia==0.8.3 scipy==1.18.1 librosa==1.0.0 soundfile==0.14.0 omegaconf==2.4.0 diffusers==0.41.0 \
      accelerate==1.15.0 conformer==0.3.2 s3tokenizer==0.3.0 resemble-perth==1.0.1 pyloudnorm==0.2.0 loguru==0.7.3 \
      pypinyin==0.55.0 py3langid==0.3.0 hangul-romanize==0.1.0 num2words==0.5.14 peft==0.21.2 spacy==3.8.16 \
 && pip install --no-cache-dir --no-deps chatterbox-tts==0.1.7 \
      https://github.com/ace-step/ACE-Step/archive/1bee4c9f5b43e30995f8d4d33b3919197ce1bd68.tar.gz

COPY server.py /app/server.py

ENV STUDIO_DIR=/models/studio \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1 \
    HF_HUB_DISABLE_TELEMETRY=1 \
    HOME=/tmp \
    NUMBA_CACHE_DIR=/tmp/numba \
    MPLCONFIGDIR=/tmp/matplotlib \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app
USER 65534:65534
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=120s \
  CMD ["python3", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=4)"]
CMD ["python3", "-m", "uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8000", "--no-access-log"]
