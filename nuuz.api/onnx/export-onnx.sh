#!/usr/bin/env bash
set -euo pipefail

# Resolve this script's directory (your onnx/ folder)
SCRIPT_DIR="$(cd -- "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"

# Defaults (override via env or flags)
MODEL="${MODEL:-sentence-transformers/all-MiniLM-L6-v2}"   # or intfloat/e5-small-v2
OPSET="${OPSET:-17}"                                       # >=14 required for SDPA; 17 is safe
OUT_DIR="${OUT_DIR:-$SCRIPT_DIR}"                          # write model.onnx here
VENV="${VENV:-$SCRIPT_DIR/.venv}"                          # venv lives next to exports

# Parse flags (and allow positional MODEL)
while [[ $# -gt 0 ]]; do
  case "$1" in
    --model) MODEL="$2"; shift 2;;
    --out)   OUT_DIR="$2"; shift 2;;
    --opset) OPSET="$2"; shift 2;;
    --venv)  VENV="$2"; shift 2;;
    -h|--help)
      echo "Usage: $0 [--model huggingface_id] [--out path] [--opset 17] [--venv path]"
      echo "       $0 intfloat/e5-small-v2"
      exit 0;;
    *) MODEL="$1"; shift;;
  esac
done

# Normalize OUT_DIR relative to script dir if not absolute
case "$OUT_DIR" in
  /*|?:/*) ;;  # absolute (/path or D:/path)
  *) OUT_DIR="$SCRIPT_DIR/$(printf '%s' "$OUT_DIR" | sed 's#^\./##')" ;;
esac
mkdir -p "$OUT_DIR"

echo "▶ Exporting encoder"
echo "   model:  $MODEL"
echo "   out:    $OUT_DIR"
echo "   opset:  $OPSET"
echo "   venv:   $VENV"

# ---- Python picker (NO spaces in the var) ----
if command -v py >/dev/null 2>&1; then
  PYCMD=(py -3)
elif command -v python >/dev/null 2>&1; then
  PYCMD=(python)
else
  PYCMD=(python3)
fi

# Create & activate venv
"${PYCMD[@]}" -m venv "$VENV"
if [[ -f "$VENV/Scripts/activate" ]]; then
  # Windows venv
  # shellcheck disable=SC1090
  source "$VENV/Scripts/activate"
else
  # WSL/Linux venv
  # shellcheck disable=SC1091
  source "$VENV/bin/activate"
fi

python -m pip install -q --upgrade pip
python -m pip install -q "transformers>=4.40" "optimum[onnxruntime]>=1.18" sentence-transformers onnx onnxruntime tokenizers

run_export () {
  local ops="$1"
  set +e
  # Module form (more reliable on Windows PATH)
  python -m optimum.exporters.onnx \
    --model "$MODEL" \
    --task feature-extraction \
    --opset "$ops" \
    "$OUT_DIR/" 2> "$OUT_DIR/export.stderr"
  local rc=$?
  set -e
  echo $rc
}

# Try with requested OPSET; if SDPA error, bump to 17 and retry
rc=$(run_export "$OPSET")
if [[ "$rc" -ne 0 ]]; then
  if grep -qi "scaled_dot_product_attention" "$OUT_DIR/export.stderr"; then
    echo "ℹ️ Detected scaled_dot_product_attention; retrying with opset 17..."
    OPSET=17
    run_export "$OPSET" >/dev/null
  else
    echo "❌ Export failed. See $OUT_DIR/export.stderr" >&2
    exit 2
  fi
fi
rm -f "$OUT_DIR/export.stderr" || true

# Save tokenizer.json alongside model.onnx
python - <<PY
from transformers import AutoTokenizer
tok = AutoTokenizer.from_pretrained("$MODEL", use_fast=True)
tok.save_pretrained(r"$OUT_DIR")
print("✓ tokenizer.json saved to $OUT_DIR")
PY

# Quick check
if [[ -f "$OUT_DIR/model.onnx" && -f "$OUT_DIR/tokenizer.json" ]]; then
  echo "✅ done: $OUT_DIR/model.onnx + tokenizer.json (opset=$OPSET)"
  echo "   tip: if you used 'intfloat/e5-small-v2', set UseE5Prefix=true in your appsettings and prepend 'passage: ' in your embedder."
else
  echo "❌ export succeeded but files not found in $OUT_DIR" >&2
  exit 3
fi