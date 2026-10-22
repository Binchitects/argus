"""
The media studio's own models over HTTP, on the GPU: a picture's background taken away
(BiRefNet lite), a text spoken in the voice of a short sample (Chatterbox), and music from
a description and lyrics (ACE-Step).

    GET  /health                          what is there, what is loaded, the GPU's free memory
    POST /v1/unload                       its models unloaded now (the app gives the GPU back to the chat models)
    POST /v1/cutout   a picture's bytes   -> image/png, its background transparent
    POST /v1/speak    {"text", "voice", "exaggeration"}                  -> audio/mpeg
    POST /v1/music    {"prompt", "lyrics", "seconds", "steps", "seed"}    -> audio/mpeg

The weights are read from the model library (STUDIO_DIR), which the app fills
(src/Llm.Api/Models/MediaModels.cs); nothing is fetched here (HF_HUB_OFFLINE). One model
works at a time; it is loaded when first asked for (another one is unloaded first) and
unloaded once unused for IDLE seconds, so the GPU goes back to the chat models. A model
that does not fit in the GPU's free memory is refused (503) with what it needs.
"""
from __future__ import annotations

import base64
import gc
import io
import os
import re
import tempfile
import threading
import time
import traceback

import numpy as np
import soundfile as sf
import torch
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, Response
from PIL import Image

STUDIO_DIR = os.environ.get("STUDIO_DIR", "/models/studio")
IDLE = float(os.environ.get("STUDIO_IDLE", "180"))
DEVICE = "cuda" if torch.cuda.is_available() else "cpu"

# Each model's folder, the files it needs (a ".part" is not one yet), and the GPU memory it takes as it works, in MB.
MODELS = {
    "cutout": ("birefnet-lite", ["model.safetensors", "birefnet.py", "BiRefNet_config.py", "config.json"], 1800),
    "voice": ("chatterbox", ["t3_cfg.safetensors", "s3gen.safetensors", "ve.safetensors", "tokenizer.json", "conds.pt"], 4500),
    "music": ("ace-step", ["ace_step_transformer/diffusion_pytorch_model.safetensors", "music_dcae_f8c8/diffusion_pytorch_model.safetensors",
                           "music_vocoder/diffusion_pytorch_model.safetensors", "umt5-base/model.safetensors"], 10000),
}

MAX_PICTURE = 30 * 1024 * 1024
MAX_SIDE = 6000
MAX_TEXT = 3000
MAX_VOICE = 12 * 1024 * 1024
MAX_SECONDS = 240
MAX_LYRICS = 6000

app = FastAPI(title="studio", docs_url=None, redoc_url=None)
_lock = threading.Lock()
_loaded: dict[str, object] = {}
_used = time.monotonic()


class Refused(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status


def _dir(kind: str) -> str:
    return os.path.join(STUDIO_DIR, MODELS[kind][0])


def _present(kind: str) -> bool:
    return all(os.path.isfile(os.path.join(_dir(kind), f)) for f in MODELS[kind][1])


def _free_mb() -> int | None:
    if DEVICE != "cuda":
        return None
    free, _ = torch.cuda.mem_get_info()
    return free // (1024 * 1024)


def _unload(kind: str) -> None:
    _loaded.pop(kind, None)
    gc.collect()
    if DEVICE == "cuda":
        torch.cuda.empty_cache()


def _load(kind: str):
    """The model, loaded when it is not (any other unloaded first: one at a time). Called under the lock."""
    if kind in _loaded:
        return _loaded[kind]
    if not _present(kind):
        raise Refused(503, f"The {kind} model is not in the library yet (Admin → Models → default models downloads it).")
    for other in list(_loaded):
        _unload(other)
    need = MODELS[kind][2]
    free = _free_mb()
    if free is not None and free < need:
        raise Refused(503, f"The GPU has {free:,} MB free; this needs about {need:,} MB. Try again when the chat models have made room.")
    path = _dir(kind)
    if kind == "cutout":
        from transformers import AutoModelForImageSegmentation
        model = AutoModelForImageSegmentation.from_pretrained(path, trust_remote_code=True).to(DEVICE).eval()
        if DEVICE == "cuda":
            model.half()
    elif kind == "voice":
        from chatterbox.tts import ChatterboxTTS
        model = ChatterboxTTS.from_local(path, DEVICE)
        # Its own voice, for speaking without a sample: a person's sample is never kept for the next one.
        model.default_conds = model.conds
    else:
        import torchaudio
        from acestep.pipeline_ace_step import ACEStepPipeline

        # ACE-Step saves its music with torchaudio, which now writes through TorchCodec (it needs FFmpeg's libraries): soundfile instead.
        torchaudio.save = lambda path, src, sample_rate, **_: sf.write(path, src.float().cpu().numpy().T, sample_rate)
        model = ACEStepPipeline(checkpoint_dir=path, dtype="bfloat16" if DEVICE == "cuda" else "float32", torch_compile=False, cpu_offload=False)
    _loaded[kind] = model
    return model


def _idle_unloader() -> None:
    while True:
        time.sleep(15)
        if _loaded and time.monotonic() - _used > IDLE and _lock.acquire(blocking=False):
            try:
                for kind in list(_loaded):
                    _unload(kind)
            finally:
                _lock.release()


threading.Thread(target=_idle_unloader, daemon=True).start()


def _work(kind: str, run):
    """One piece of work at a time, with its model loaded; the time it was last used kept for unloading it."""
    global _used
    with _lock:
        try:
            model = _load(kind)
            with torch.inference_mode():
                return run(model)
        finally:
            _used = time.monotonic()


def _error(status: int, message: str) -> JSONResponse:
    return JSONResponse({"error": message}, status_code=status)


def _mp3(wave: np.ndarray, rate: int) -> bytes:
    out = io.BytesIO()
    sf.write(out, wave, rate, format="MP3")
    return out.getvalue()


@app.get("/health")
def health():
    return {
        "ok": True,
        "device": DEVICE,
        "free_mb": _free_mb(),
        "models": {k: _present(k) for k in MODELS},
        "loaded": list(_loaded),
    }


@app.post("/v1/unload")
def unload():
    with _lock:
        for kind in list(_loaded):
            _unload(kind)
    return {"loaded": []}


@app.exception_handler(Refused)
async def refused(_: Request, e: Refused):
    return _error(e.status, str(e))


@app.post("/v1/cutout")
async def cutout(request: Request):
    body = await request.body()
    if not body or len(body) > MAX_PICTURE:
        return _error(400, "Send a picture of at most 30 MB.")
    try:
        picture = Image.open(io.BytesIO(body))
        picture.load()
    except Exception:
        return _error(400, "This is not a picture this reads (PNG, JPEG, WebP).")
    if max(picture.size) > MAX_SIDE:
        return _error(400, f"The picture is larger than {MAX_SIDE} pixels a side.")
    rgb = picture.convert("RGB")

    def run(model):
        x = torch.from_numpy(np.asarray(rgb.resize((1024, 1024), Image.BILINEAR), dtype=np.float32) / 255.0)
        x = (x - torch.tensor([0.485, 0.456, 0.406])) / torch.tensor([0.229, 0.224, 0.225])
        x = x.permute(2, 0, 1).unsqueeze(0).to(DEVICE)
        if DEVICE == "cuda":
            x = x.half()
        mask = model(x)[-1].sigmoid()[0, 0].float().cpu().numpy()
        return Image.fromarray((mask * 255).clip(0, 255).astype(np.uint8)).resize(rgb.size, Image.BILINEAR)

    alpha = _work("cutout", run)
    out = rgb.copy()
    out.putalpha(alpha)
    png = io.BytesIO()
    out.save(png, "PNG", optimize=False, compress_level=6)
    return Response(png.getvalue(), media_type="image/png")


_SENTENCE = re.compile(r"(?<=[.!?…])\s+")


def _pieces(text: str, most: int = 280) -> list[str]:
    """The text in pieces the voice model reads well: sentences, put together up to a few hundred characters."""
    pieces, now = [], ""
    for sentence in _SENTENCE.split(text.strip()):
        while len(sentence) > most:
            cut = sentence.rfind(" ", 0, most)
            cut = most if cut < most // 2 else cut
            if now:
                pieces.append(now)
                now = ""
            pieces.append(sentence[:cut].strip())
            sentence = sentence[cut:].strip()
        if now and len(now) + 1 + len(sentence) > most:
            pieces.append(now)
            now = sentence
        else:
            now = f"{now} {sentence}".strip()
    if now:
        pieces.append(now)
    return [p for p in pieces if p]


@app.post("/v1/speak")
async def speak(request: Request):
    try:
        body = await request.json()
    except Exception:
        return _error(400, "Send JSON: text, and voice (a sample, base64).")
    text = str(body.get("text") or "").strip()
    if not text or len(text) > MAX_TEXT:
        return _error(400, f"Say what to speak, in at most {MAX_TEXT:,} characters.")
    exaggeration = min(1.5, max(0.25, float(body.get("exaggeration") or 0.5)))
    sample = None
    if body.get("voice"):
        try:
            raw = base64.b64decode(body["voice"], validate=False)
        except Exception:
            return _error(400, "The voice sample is not base64.")
        if len(raw) > MAX_VOICE:
            return _error(400, "The voice sample is larger than 12 MB: a few seconds to a minute is enough.")
        try:
            wave, rate = sf.read(io.BytesIO(raw), dtype="float32", always_2d=False)
        except Exception:
            return _error(400, "The voice sample is not a sound this reads (MP3, WAV, FLAC, OGG).")
        if wave.ndim > 1:
            wave = wave.mean(axis=1)
        if len(wave) < rate * 3:
            return _error(400, "The voice sample is shorter than 3 seconds: record at least 5 to 10 seconds of speech.")
        sample = (wave[: rate * 40], rate)

    def run(model):
        path = None
        try:
            model.conds = model.default_conds
            if sample is not None:
                handle, path = tempfile.mkstemp(suffix=".wav")
                os.close(handle)
                sf.write(path, sample[0], sample[1])
                model.prepare_conditionals(path, exaggeration=exaggeration)
            elif model.conds is None:
                raise Refused(400, "Give a voice sample to speak in.")
            parts = []
            gap = np.zeros(int(model.sr * 0.18), dtype=np.float32)
            for piece in _pieces(text):
                wav = model.generate(piece, exaggeration=exaggeration, cfg_weight=0.5)
                parts.extend([wav.squeeze(0).float().cpu().numpy(), gap])
            return np.concatenate(parts[:-1]) if parts else gap, model.sr
        finally:
            model.conds = model.default_conds
            if path:
                os.remove(path)

    wave, rate = _work("voice", run)
    return Response(_mp3(wave, rate), media_type="audio/mpeg", headers={"X-Seconds": f"{len(wave) / rate:.2f}"})


@app.post("/v1/music")
async def music(request: Request):
    try:
        body = await request.json()
    except Exception:
        return _error(400, "Send JSON: prompt, lyrics, seconds.")
    prompt = str(body.get("prompt") or "").strip()
    lyrics = str(body.get("lyrics") or "").strip() or "[instrumental]"
    if not prompt or len(prompt) > 1000:
        return _error(400, "Describe the music (its style, instruments, mood, tempo) in at most 1,000 characters.")
    if len(lyrics) > MAX_LYRICS:
        return _error(400, f"The lyrics are longer than {MAX_LYRICS:,} characters.")
    seconds = min(MAX_SECONDS, max(5.0, float(body.get("seconds") or 30)))
    steps = min(100, max(20, int(body.get("steps") or 60)))
    seed = int(body.get("seed") or int.from_bytes(os.urandom(4), "little"))

    def run(model):
        out = tempfile.mkdtemp()
        try:
            model(format="wav", audio_duration=seconds, prompt=prompt, lyrics=lyrics, infer_step=steps, manual_seeds=[seed], save_path=os.path.join(out, "music.wav"))
            files = [os.path.join(out, f) for f in os.listdir(out) if f.endswith(".wav")]
            if not files:
                raise Refused(500, "The music model made nothing.")
            return sf.read(files[0], dtype="float32", always_2d=False)
        finally:
            for f in os.listdir(out):
                os.remove(os.path.join(out, f))
            os.rmdir(out)

    wave, rate = _work("music", run)
    return Response(_mp3(wave, rate), media_type="audio/mpeg", headers={"X-Seconds": f"{len(wave) / rate:.2f}", "X-Seed": str(seed)})


@app.exception_handler(Exception)
async def failed(_: Request, e: Exception):
    traceback.print_exc()
    if isinstance(e, torch.OutOfMemoryError):
        with _lock:
            for kind in list(_loaded):
                _unload(kind)
        return _error(503, "The GPU ran out of memory for this. Try again when the chat models have made room.")
    return _error(500, f"{type(e).__name__}: {e}")
