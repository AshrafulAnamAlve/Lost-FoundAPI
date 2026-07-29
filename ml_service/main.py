"""
Lost & Found — ML Service
=========================

Self-hosted embedding service for the Lost & Found matching engine.

Why this exists
---------------
The API previously called HuggingFace's hosted inference endpoint
(`api-inference.huggingface.co`). That endpoint has been decommissioned, so every
embedding call failed and `ItemSimilarityService` silently degraded to rule-based
scoring only. Hosting the model here removes the dependency entirely:

  * no API key to leak, expire or rate-limit
  * works offline (model weights are cached locally after the first run)
  * batch encoding, so building the evaluation benchmark is fast
  * this is also where the image-recognition model will live (see /embed/image)

Run
---
    python -m venv .venv
    .venv\\Scripts\\Activate.ps1
    pip install -r requirements.txt
    python main.py                    # -> http://localhost:8000

The first run downloads ~90 MB of model weights; afterwards it is fully offline.
"""

from __future__ import annotations

import base64
import io
import json
import logging
import os
import ssl
import time
import urllib.request
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Optional

from fastapi import FastAPI
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-7s  %(message)s",
)
log = logging.getLogger("ml_service")

MODELS_DIR = Path(__file__).parent / "models"

TEXT_MODEL_NAME = os.getenv("TEXT_MODEL", "sentence-transformers/all-MiniLM-L6-v2")

# Drop the exported model here and it is picked up on the next restart.
# Nothing else needs configuring - the input shape is read from the ONNX file itself.
IMAGE_MODEL_PATH = os.getenv("IMAGE_MODEL_PATH", str(MODELS_DIR / "item_model.onnx"))
IMAGE_LABELS_PATH = os.getenv("IMAGE_LABELS_PATH", str(MODELS_DIR / "labels.json"))
IMAGE_PREPROCESS_PATH = os.getenv("IMAGE_PREPROCESS_PATH", str(MODELS_DIR / "preprocess.json"))

# Populated on startup. Kept in a dict so /health can report load state and any error
# without the endpoints having to guess.
state: dict[str, object] = {
    "text_model": None,
    "text_dim": 0,
    "text_error": None,
    "image_model": None,          # onnxruntime.InferenceSession
    "image_error": "not_configured",
    "image_labels": [],
    "image_input_name": None,
    "image_layout": None,         # "NCHW" (PyTorch-style) | "NHWC" (Keras-style)
    "image_size": None,           # (height, width)
    "image_preprocess": None,
    "image_embed_output": None,   # index of an embedding output, when the model exposes one
    "started_at": None,
}


@asynccontextmanager
async def lifespan(_: FastAPI):
    """Load models once at startup rather than per request."""
    state["started_at"] = time.time()

    try:
        from sentence_transformers import SentenceTransformer

        log.info("Loading text model %s ...", TEXT_MODEL_NAME)
        began = time.time()
        model = SentenceTransformer(TEXT_MODEL_NAME)
        state["text_model"] = model
        state["text_dim"] = int(model.get_sentence_embedding_dimension())
        log.info(
            "Text model ready (dim=%s) in %.1fs",
            state["text_dim"],
            time.time() - began,
        )
    except Exception as exc:  # noqa: BLE001 - surfaced via /health, never crashes the service
        state["text_error"] = f"{type(exc).__name__}: {exc}"
        log.error("Text model failed to load: %s", state["text_error"])

    load_image_model()

    yield

    log.info("Shutting down.")


def load_image_model() -> None:
    """
    Loads the exported item classifier, if one has been placed in models/.

    Deliberately self-configuring: the input size and tensor layout are read from
    the ONNX file rather than hard-coded, so re-exporting with a different
    backbone does not require editing this service.
    """
    model_path = Path(IMAGE_MODEL_PATH)
    if not model_path.exists():
        state["image_error"] = (
            f"no model at {model_path} - see models/README.md for how to export one"
        )
        log.info("Image model not present (%s). Image endpoints will report 503.", model_path.name)
        return

    try:
        import onnxruntime

        session = onnxruntime.InferenceSession(
            str(model_path), providers=["CPUExecutionProvider"]
        )

        inp = session.get_inputs()[0]
        shape = inp.shape  # e.g. [None, 224, 224, 3] (Keras) or [None, 3, 224, 224] (PyTorch)
        if len(shape) != 4:
            raise ValueError(f"expected a 4-D image input, got shape {shape}")

        # Channels-first if dim 1 is 1 or 3; otherwise assume channels-last (Keras default).
        if isinstance(shape[1], int) and shape[1] in (1, 3):
            layout, height, width = "NCHW", shape[2], shape[3]
        else:
            layout, height, width = "NHWC", shape[1], shape[2]

        if not isinstance(height, int) or not isinstance(width, int):
            height = width = int(os.getenv("IMAGE_SIZE", "224"))
            log.warning("Model has a dynamic input size; defaulting to %dx%d.", height, width)

        # Labels: index -> class name. Without them we can still return indices.
        labels: list[str] = []
        labels_path = Path(IMAGE_LABELS_PATH)
        if labels_path.exists():
            loaded = json.loads(labels_path.read_text(encoding="utf-8"))
            if isinstance(loaded, dict):
                labels = [loaded[k] for k in sorted(loaded, key=lambda x: int(x))]
            else:
                labels = list(loaded)

        # Preprocessing must match what was used during training.
        preprocess = {"rescale": 1.0 / 255.0, "mean": None, "std": None}
        pre_path = Path(IMAGE_PREPROCESS_PATH)
        if pre_path.exists():
            preprocess.update(json.loads(pre_path.read_text(encoding="utf-8")))

        # If the export exposes a second, wider output, treat it as an embedding.
        embed_output = None
        outputs = session.get_outputs()
        if len(outputs) > 1:
            widest = max(
                range(len(outputs)),
                key=lambda i: outputs[i].shape[-1] if isinstance(outputs[i].shape[-1], int) else 0,
            )
            if widest != 0:
                embed_output = widest

        state["image_model"] = session
        state["image_labels"] = labels
        state["image_input_name"] = inp.name
        state["image_layout"] = layout
        state["image_size"] = (height, width)
        state["image_preprocess"] = preprocess
        state["image_embed_output"] = embed_output
        state["image_error"] = None

        log.info(
            "Image model ready: %s (%s %dx%d, %d labels%s)",
            model_path.name, layout, height, width, len(labels),
            ", + embedding output" if embed_output is not None else "",
        )
    except Exception as exc:  # noqa: BLE001 - reported via /health, never fatal
        state["image_error"] = f"{type(exc).__name__}: {exc}"
        log.error("Image model failed to load: %s", state["image_error"])


def _fetch_image_bytes(ref: str) -> bytes:
    """Accepts a data: URI, a bare base64 string, or an http(s) URL."""
    if ref.startswith("data:"):
        ref = ref.split(",", 1)[1]
    if ref.startswith("http://") or ref.startswith("https://"):
        # The API serves uploads over HTTPS with a dev certificate.
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        with urllib.request.urlopen(ref, context=ctx, timeout=15) as r:
            return r.read()
    return base64.b64decode(ref)


def _preprocess(raw: bytes):
    import numpy as np
    from PIL import Image

    height, width = state["image_size"]  # type: ignore[misc]
    pre = state["image_preprocess"]      # type: ignore[assignment]

    img = Image.open(io.BytesIO(raw)).convert("RGB").resize((width, height))
    arr = np.asarray(img, dtype=np.float32) * float(pre["rescale"])

    if pre.get("mean") is not None:
        arr = (arr - np.array(pre["mean"], dtype=np.float32)) / np.array(
            pre["std"] or [1, 1, 1], dtype=np.float32
        )

    if state["image_layout"] == "NCHW":
        arr = arr.transpose(2, 0, 1)
    return arr


app = FastAPI(
    title="Lost & Found ML Service",
    description="Text (and later image) embeddings for the item-matching engine.",
    version="1.0.0",
    lifespan=lifespan,
)


# ---------------------------------------------------------------------------
# Schemas
# ---------------------------------------------------------------------------

class EmbedTextRequest(BaseModel):
    texts: list[str] = Field(
        ...,
        min_length=1,
        max_length=512,
        description="Batch of texts to embed. Batching matters: encoding 300 texts "
                    "in one call is far cheaper than 300 calls.",
    )


class EmbedTextResponse(BaseModel):
    model: str
    dim: int
    vectors: list[list[float]]
    tookMs: float


class SimilarityRequest(BaseModel):
    left: str
    right: str


class SimilarityResponse(BaseModel):
    model: str
    cosine: float


# ---------------------------------------------------------------------------
# Endpoints
# ---------------------------------------------------------------------------

@app.get("/health")
def health():
    """
    Reported by the .NET API on /api/health so an embedding outage can never again
    go unnoticed. `textReady: false` means matching has silently fallen back to
    rule-based scoring.
    """
    ready = state["text_model"] is not None
    return {
        "status": "ok" if ready else "degraded",
        "textReady": ready,
        "textModel": TEXT_MODEL_NAME,
        "textDim": state["text_dim"],
        "textError": state["text_error"],
        "imageReady": state["image_model"] is not None,
        "imageError": state["image_error"],
        "imageLabels": len(state["image_labels"] or []),
        "imageSize": state["image_size"],
        "imageLayout": state["image_layout"],
        "imageHasEmbedding": state["image_embed_output"] is not None,
        "uptimeSeconds": round(time.time() - float(state["started_at"] or time.time()), 1),
    }


@app.post("/embed/text", response_model=EmbedTextResponse)
def embed_text(req: EmbedTextRequest):
    """
    Returns L2-normalised embeddings, so cosine similarity on the .NET side is a
    plain dot product and no renormalisation is needed there.
    """
    model = state["text_model"]
    if model is None:
        return JSONResponse(
            status_code=503,
            content={"error": "text model unavailable", "detail": state["text_error"]},
        )

    began = time.perf_counter()
    vectors = model.encode(
        req.texts,
        normalize_embeddings=True,
        batch_size=32,
        show_progress_bar=False,
    )
    took = (time.perf_counter() - began) * 1000

    return EmbedTextResponse(
        model=TEXT_MODEL_NAME,
        dim=int(state["text_dim"]),
        vectors=[[float(x) for x in vec] for vec in vectors],
        tookMs=round(took, 2),
    )


@app.post("/similarity", response_model=SimilarityResponse)
def similarity(req: SimilarityRequest):
    """Convenience endpoint for manual testing and for the evaluation harness."""
    model = state["text_model"]
    if model is None:
        return JSONResponse(
            status_code=503,
            content={"error": "text model unavailable", "detail": state["text_error"]},
        )

    left, right = model.encode([req.left, req.right], normalize_embeddings=True)
    cosine = float(sum(a * b for a, b in zip(left, right)))
    return SimilarityResponse(model=TEXT_MODEL_NAME, cosine=round(cosine, 6))


class ClassifyRequest(BaseModel):
    images: list[str] = Field(
        ...,
        min_length=1,
        max_length=32,
        description="Each entry may be a data: URI, a bare base64 string, or an http(s) URL.",
    )
    topK: int = Field(3, ge=1, le=20)


@app.post("/embed/image")
def embed_image(req: ClassifyRequest):
    """
    Classifies item photos, and returns an embedding too when the exported model
    exposes one.

    Response:
        {
          "model": "item_model.onnx",
          "labels": [[{"category": "phone", "confidence": 0.94}, ...], ...],  # topK per image
          "vectors": [[...], ...] | null,     # L2-normalised, when available
          "dim": 512 | 0
        }

    Two outputs, two purposes:
      * labels  -> auto-suggest the category, and catch user mis-categorisation.
                   This matters a lot here: the .NET gate multiplies a cross-category
                   pair by 0.20, so one wrong dropdown choice destroys a real match.
      * vectors -> visual similarity between a lost photo and a found photo.
    """
    session = state["image_model"]
    if session is None:
        return JSONResponse(
            status_code=503,
            content={"error": "image model not configured", "detail": state["image_error"]},
        )

    import numpy as np

    began = time.perf_counter()
    batch = np.stack([_preprocess(_fetch_image_bytes(ref)) for ref in req.images])
    outputs = session.run(None, {state["image_input_name"]: batch})

    logits = np.asarray(outputs[0], dtype=np.float32)
    # Apply softmax only if the model did not already output probabilities.
    if not np.allclose(logits.sum(axis=-1), 1.0, atol=1e-3):
        shifted = logits - logits.max(axis=-1, keepdims=True)
        exp = np.exp(shifted)
        logits = exp / exp.sum(axis=-1, keepdims=True)

    labels = state["image_labels"] or []
    top: list[list[dict]] = []
    for row in logits:
        order = np.argsort(row)[::-1][: req.topK]
        top.append([
            {
                "category": labels[i] if i < len(labels) else str(int(i)),
                "confidence": round(float(row[i]), 4),
            }
            for i in order
        ])

    vectors = None
    dim = 0
    idx = state["image_embed_output"]
    if idx is not None:
        emb = np.asarray(outputs[idx], dtype=np.float32)
        norms = np.linalg.norm(emb, axis=-1, keepdims=True)
        emb = emb / np.clip(norms, 1e-9, None)
        vectors = emb.tolist()
        dim = emb.shape[-1]

    return {
        "model": Path(IMAGE_MODEL_PATH).name,
        "labels": top,
        "vectors": vectors,
        "dim": dim,
        "tookMs": round((time.perf_counter() - began) * 1000, 2),
    }


if __name__ == "__main__":
    import uvicorn

    port = int(os.getenv("PORT", "8000"))
    uvicorn.run(app, host="127.0.0.1", port=port, log_level="info")
