# ML Service — Lost & Found

Self-hosted embedding service that powers the **semantic layer** of the item-matching
engine. The .NET API calls it; if it is down, matching silently falls back to
rule-based scoring only (and `/api/health` will say so).

---

## Why this service exists

The API originally called HuggingFace's hosted inference endpoint,
`api-inference.huggingface.co`. That endpoint has since been **decommissioned**, so
every embedding request failed. Because `CalculateAiScoreAsync` caught the error and
returned `-1`, `ComputeScoreAsync` took its rules-only branch and nothing ever
reported a problem — matching had been running **without its semantic layer**.

Hosting the model locally removes the whole class of failure:

| | Hosted API | This service |
|---|---|---|
| API key | required, leakable, revocable | none |
| Rate limits | yes | none |
| Works offline | no | **yes** (after first run) |
| Cost | quota-bound | free |
| Latency | network round trip | in-process, batched |
| Hosts the image model too | no | **yes** (`/embed/image`) |

---

## Run it

```powershell
cd ml_service
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python main.py
```

Or just double-click **`start.ps1`** (creates the venv and installs on first run).

Service listens on **http://localhost:8000**.
The first start downloads ~90 MB of model weights into `%USERPROFILE%\.cache\huggingface`.
Every start after that is fully offline.

> **Start this before the .NET API** so the semantic layer is available from the first request.

---

## Endpoints

### `GET /health`
```json
{ "status": "ok", "textReady": true, "textModel": "sentence-transformers/all-MiniLM-L6-v2",
  "textDim": 384, "textError": null, "imageReady": false, "imageError": "not_configured" }
```
`textReady: false` means the API is matching on rules alone.

### `POST /embed/text`
```json
// request
{ "texts": ["black dell laptop", "dell notebook, dark coloured"] }

// response — vectors are L2-normalised, so cosine == dot product
{ "model": "sentence-transformers/all-MiniLM-L6-v2", "dim": 384,
  "vectors": [[0.021, -0.043, ...], [...]], "tookMs": 18.4 }
```
Send texts in **batches** — one call for 300 texts is far cheaper than 300 calls.

### `POST /similarity`
Convenience endpoint for manual testing and the evaluation harness.
```json
{ "left": "black wallet", "right": "dark brown purse" }   →   { "cosine": 0.61 }
```

### `POST /embed/image` — reserved
Returns `503 not_configured` until the trained image model is dropped in. See below.

---

## Adding your trained image model

The .NET side is already designed against this contract, so nothing in the API has to
change when you plug the model in:

```jsonc
// request
{ "imageUrls": ["/uploads/lost/abc.jpg", "/uploads/found/xyz.jpg"] }

// response
{
  "model": "...", "modelVersion": "v1", "dim": 512,
  "vectors": [[...], [...]],                          // L2-normalised
  "labels":  [{ "category": "phone", "confidence": 0.94 }]
}
```

Return **both** outputs — they do different jobs:

- **`vectors`** → visual similarity between a lost photo and a found photo. Catches
  matches that text alone misses entirely.
- **`labels`** → auto-suggest the category on the post form and flag user
  mis-categorisation. This is a real data-quality win and a good demo moment.

To wire it up: set the `IMAGE_MODEL_PATH` environment variable and implement the
loader in the marked block in `main.py`.

**Design rule to keep:** image similarity should *boost and re-rank*, never create a
match on its own — two different black iPhones look identical to a vision model.
Gate it by category.

---

## Configuration

| Env var | Default | Purpose |
|---|---|---|
| `TEXT_MODEL` | `sentence-transformers/all-MiniLM-L6-v2` | Swap the text encoder |
| `IMAGE_MODEL_PATH` | *(unset)* | Enables the image model loader |
| `PORT` | `8000` | Listen port |

The .NET side points here via `Embedding:ServiceUrl` in `appsettings.json`.
Set it to `""` to disable this service and fall back to HuggingFace.
