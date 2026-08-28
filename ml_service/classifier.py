"""
Lost & Found — item image classifier
====================================

Wraps the exported MobileNetV2 device classifier that lives in models/.

Why this is a separate module from the embedding code in main.py
---------------------------------------------------------------
`/embed/image` was written earlier, speculatively, for a *generic* exported model:
it reads models/item_model.onnx and applies a rescale factor from
models/preprocess.json, defaulting to 1/255. This model needs the opposite —
see the preprocessing note below — so pointing that endpoint at it would have
silently produced wrong predictions. This module owns its own session, its own
files and its own endpoint, and leaves the existing one untouched.

The three files it loads (all in models/, all required):

    model.onnx        MobileNetV2, 4 classes
    class_names.json  ["Calculator", "Laptop", "Mobile Phone", "Watch"]
    config.json       input/output tensor names, input size, threshold

Nothing here is hard-coded from those files: change the export and this module
follows it, so long as the three files stay in step with each other.
"""

from __future__ import annotations

import io
import json
import logging
import os
import threading
import time
from pathlib import Path
from typing import Any, Optional

log = logging.getLogger("ml_service.classifier")

MODELS_DIR = Path(__file__).parent / "models"

MODEL_PATH = os.getenv("CLASSIFIER_MODEL_PATH", str(MODELS_DIR / "model.onnx"))
CLASS_NAMES_PATH = os.getenv("CLASSIFIER_CLASSES_PATH", str(MODELS_DIR / "class_names.json"))
CONFIG_PATH = os.getenv("CLASSIFIER_CONFIG_PATH", str(MODELS_DIR / "config.json"))

# Used only if config.json omits it. The exported model recommends 0.65.
FALLBACK_THRESHOLD = 0.65

# Loaded once, at startup, and then read concurrently by request handlers.
# onnxruntime sessions are thread-safe for run(); the lock only guards loading.
_load_lock = threading.Lock()

_state: dict[str, Any] = {
    "session": None,
    "error": "not_loaded",
    "class_names": [],
    "input_name": None,
    "output_name": None,
    "input_size": None,       # (height, width)
    "threshold": FALLBACK_THRESHOLD,
    "loaded_at": None,
}


def load() -> None:
    """
    Loads the ONNX session once. Safe to call again; later calls are no-ops
    unless the previous attempt failed, in which case it retries.

    Never raises: a failure is recorded in _state and surfaced through /health
    and a 503 from the endpoint. The service must still start and serve text
    embeddings when the image model is missing or broken.
    """
    with _load_lock:
        if _state["session"] is not None:
            return

        try:
            model_path = Path(MODEL_PATH)
            if not model_path.exists():
                _state["error"] = f"no model at {model_path}"
                log.info("Classifier not present (%s); /classify/image will report 503.", model_path.name)
                return

            # Class order defines the meaning of every output index, so it is read
            # from the file and never assumed. Reordering class_names.json without
            # re-exporting the model would mislabel every prediction.
            names_path = Path(CLASS_NAMES_PATH)
            if not names_path.exists():
                _state["error"] = f"no class names at {names_path}"
                log.error("Classifier class names missing: %s", names_path)
                return

            class_names = json.loads(names_path.read_text(encoding="utf-8"))
            if not isinstance(class_names, list) or not class_names:
                _state["error"] = f"{names_path.name} must contain a non-empty JSON array"
                log.error("Classifier class names malformed: %s", _state["error"])
                return

            config: dict[str, Any] = {}
            config_path = Path(CONFIG_PATH)
            if config_path.exists():
                config = json.loads(config_path.read_text(encoding="utf-8"))

            import onnxruntime

            session = onnxruntime.InferenceSession(
                str(model_path), providers=["CPUExecutionProvider"]
            )

            # Prefer the names the export documented; fall back to whatever the
            # graph actually declares, so a re-export cannot break this on a rename.
            graph_input = session.get_inputs()[0]
            graph_output = session.get_outputs()[0]
            input_name = config.get("input_name") or graph_input.name
            output_name = config.get("output_name") or graph_output.name

            size = config.get("input_size")
            if isinstance(size, (list, tuple)) and len(size) == 2:
                height, width = int(size[0]), int(size[1])
            else:
                # [N, H, W, C] — the batch dimension is dynamic, H and W are not.
                shape = graph_input.shape
                height, width = int(shape[1]), int(shape[2])

            declared = graph_output.shape[-1]
            if isinstance(declared, int) and declared != len(class_names):
                _state["error"] = (
                    f"model outputs {declared} classes but class_names.json lists "
                    f"{len(class_names)} — these must agree"
                )
                log.error("Classifier mismatch: %s", _state["error"])
                return

            _state.update(
                session=session,
                error=None,
                class_names=list(class_names),
                input_name=input_name,
                output_name=output_name,
                input_size=(height, width),
                threshold=float(config.get("recommended_threshold", FALLBACK_THRESHOLD)),
                loaded_at=time.time(),
            )

            log.info(
                "Classifier ready: %s (%dx%d, %d classes: %s, threshold %.2f)",
                model_path.name, height, width, len(class_names),
                ", ".join(class_names), _state["threshold"],
            )
        except Exception as exc:  # noqa: BLE001 - reported via /health, never fatal
            _state["error"] = f"{type(exc).__name__}: {exc}"
            log.error("Classifier failed to load: %s", _state["error"])


def is_ready() -> bool:
    return _state["session"] is not None


def status() -> dict[str, Any]:
    """Shape mirrors what /health already reports for the other models."""
    return {
        "ready": is_ready(),
        "error": _state["error"],
        "model": Path(MODEL_PATH).name,
        "classes": list(_state["class_names"]),
        "inputSize": _state["input_size"],
        "threshold": _state["threshold"],
    }


def threshold() -> float:
    return float(_state["threshold"])


class ImageDecodeError(ValueError):
    """Raised when the bytes handed in are not a usable image."""


def _preprocess(raw: bytes):
    """
    Resizes to the model's input size and returns RAW 0-255 float32.

    ────────────────────────────────────────────────────────────────────────
    DO NOT NORMALIZE HERE. NO /255. NO (x/127.5 - 1). NO mean/std subtraction.
    ────────────────────────────────────────────────────────────────────────
    This export carries its own preprocessing inside the graph (a TrueDivide
    followed by a Subtract, right after the input node), so the model expects
    plain pixel values in [0, 255]. Normalizing here would scale the input a
    second time.

    That mistake does not raise — the shapes and dtype stay valid, inference
    still returns four probabilities, and they are simply wrong. There is no
    error message to notice, so the only defence is this comment. If you are
    here to "fix" the missing rescale: it is missing on purpose.
    (models/config.json records the same thing: "preprocessing": "internal".)
    """
    import numpy as np
    from PIL import Image

    height, width = _state["input_size"]

    try:
        img = Image.open(io.BytesIO(raw))
        img.load()
        img = img.convert("RGB").resize((width, height))
    except Exception as exc:  # noqa: BLE001 - unreadable upload, not a server fault
        raise ImageDecodeError(f"{type(exc).__name__}: {exc}") from exc

    # float32 in [0, 255], exactly as stored. See the warning above.
    return np.asarray(img, dtype=np.float32)


def classify(images: list[bytes], top_k: int = 4) -> list[dict[str, Any]]:
    """
    Classifies a batch of images.

    Returns one result per input, in order:

        {
          "label": "Laptop" | None,     # None when below the threshold
          "confidence": 0.9312,         # of the top class, whatever it was
          "known": True,                # confidence >= threshold
          "scores": {"Calculator": 0.01, "Laptop": 0.93, ...},
          "topK": [{"label": "Laptop", "confidence": 0.9312}, ...],
          "error": None                 # or a message for an undecodable image
        }

    A single corrupt image yields an entry with error set rather than failing
    the whole batch — one bad upload should not take out the others.
    """
    import numpy as np

    if not is_ready():
        raise RuntimeError(_state["error"] or "classifier not loaded")

    class_names: list[str] = _state["class_names"]
    limit = max(1, min(int(top_k), len(class_names)))

    # Decode first so a corrupt image is reported per-index instead of
    # collapsing the batch. Failed slots are held out of the tensor entirely.
    decoded: list[Optional[Any]] = []
    errors: list[Optional[str]] = []
    for raw in images:
        try:
            decoded.append(_preprocess(raw))
            errors.append(None)
        except ImageDecodeError as exc:
            decoded.append(None)
            errors.append(str(exc))
        except Exception as exc:  # noqa: BLE001 - defensive; same handling
            decoded.append(None)
            errors.append(f"{type(exc).__name__}: {exc}")

    usable = [i for i, arr in enumerate(decoded) if arr is not None]
    probabilities: dict[int, Any] = {}

    if usable:
        batch = np.stack([decoded[i] for i in usable])
        outputs = _state["session"].run([_state["output_name"]], {_state["input_name"]: batch})
        rows = np.asarray(outputs[0], dtype=np.float32)

        # The export ends in softmax, so these are already probabilities. Only
        # normalise if a future re-export emits raw logits instead.
        if not np.allclose(rows.sum(axis=-1), 1.0, atol=1e-3):
            shifted = rows - rows.max(axis=-1, keepdims=True)
            exponentiated = np.exp(shifted)
            rows = exponentiated / exponentiated.sum(axis=-1, keepdims=True)

        for slot, index in enumerate(usable):
            probabilities[index] = rows[slot]

    cutoff = threshold()
    results: list[dict[str, Any]] = []

    for index in range(len(images)):
        if index not in probabilities:
            results.append({
                "label": None,
                "confidence": 0.0,
                "known": False,
                "scores": {},
                "topK": [],
                "error": errors[index] or "image could not be read",
            })
            continue

        row = probabilities[index]
        order = np.argsort(row)[::-1]
        best = int(order[0])
        confidence = float(row[best])

        results.append({
            # Below the threshold the model is guessing, so it names nothing and
            # the caller asks the user instead. The confidence is still reported
            # so the UI can explain why it is asking.
            "label": class_names[best] if confidence >= cutoff else None,
            "confidence": round(confidence, 4),
            "known": confidence >= cutoff,
            "scores": {class_names[i]: round(float(row[i]), 4) for i in range(len(class_names))},
            "topK": [
                {"label": class_names[int(i)], "confidence": round(float(row[int(i)]), 4)}
                for i in order[:limit]
            ],
            "error": None,
        })

    return results
