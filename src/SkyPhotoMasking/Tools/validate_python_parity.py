"""Independent Python oracle for C# diagnostics; uses original crop/fix_circle function ASTs.
Usage: python validate_python_parity.py WEIGHTS ORIGINAL_INFERENCE --validation ../Validator
The C# validator must first run with --diagnostics. No training is performed.
"""
import argparse
import ast
import json
import tempfile
import time
from pathlib import Path
from typing import List, Tuple

import cv2
import numpy as np
import torch
import yaml
from export_models import load_model


def main():
    p = argparse.ArgumentParser()
    p.add_argument("weights", type=Path)
    p.add_argument("original_inference", type=Path)
    p.add_argument("--validation", type=Path, default=Path(__file__).resolve().parents[1] / "Validator")
    args = p.parse_args()
    report = json.loads((args.validation / "validation-report.json").read_text(encoding="utf-8-sig"))
    source = cv2.imread(report["imagePath"])
    rgb = cv2.cvtColor(source, cv2.COLOR_BGR2RGB)
    # Execute exactly the original helpers without loading interactive pipeline dependencies.
    tree = ast.parse(args.original_inference.read_text(encoding="utf-8-sig"))
    functions = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in ("crop_around_disk", "fix_circle")]
    ns = {"np": np, "yml": yaml, "Tuple": Tuple, "List": List}
    exec(compile(ast.Module(body=functions, type_ignores=[]), str(args.original_inference), "exec"), ns)
    torch.set_num_threads(8)
    results = []
    for record in report["records"]:
        if "error" in record:
            raise RuntimeError(record["error"])
        result = record["warm"]
        n = result["InputSize"]
        variant = record["model"][-2:].lower()
        disk = result["Disk"]
        with tempfile.TemporaryDirectory() as temp:
            pp = Path(temp)/"pprad.yml"
            pp.write_text(yaml.safe_dump({"principal_point": [disk["CenterX"], disk["CenterY"]], "radius": round(disk["Radius"])}))
            crop, section = ns["crop_around_disk"](str(pp), rgb)
        image = cv2.resize(crop, (n, n), interpolation=cv2.INTER_LINEAR)
        stem = Path(record["maskPath"]).stem
        diagnostic = args.validation/"Diagnostics"
        cs_image = cv2.cvtColor(cv2.imread(str(diagnostic/(stem+"-input.png"))), cv2.COLOR_BGR2RGB)
        assert np.array_equal(image, cs_image), "C# and Python preprocessing differ"
        c = result["Crop"]
        assert section == [c["X"], c["X"]+c["Width"]-1, c["Y"], c["Y"]+c["Height"]-1]
        model = load_model(args.weights/f"efficientnet-{variant}.pt", variant)
        # Restore the original EfficientNet activation implementation for the oracle.
        model.encoder.set_swish(memory_efficient=True)
        tensor = torch.tensor(image, dtype=torch.float32).permute(2, 0, 1).unsqueeze(0)/255.0
        start = time.perf_counter()
        with torch.no_grad():
            prediction = model(tensor).squeeze().numpy()
        elapsed = time.perf_counter()-start
        cs_prob = np.fromfile(diagnostic/(stem+".f32"), dtype="<f4").reshape(n, n)
        threshold = {"b4":155, "b5":158, "b6":148, "b7":161}[variant]/255
        pred_mask = 1-(prediction > threshold).astype(np.uint8)
        pred_mask = ns["fix_circle"](pred_mask, size=n)
        restored = cv2.resize(pred_mask, (crop.shape[1], crop.shape[0]), interpolation=cv2.INTER_NEAREST)
        full = np.zeros(rgb.shape[:2], dtype=np.uint8)
        full[section[2]:section[3]+1, section[0]:section[1]+1] = restored*255
        cs_mask = cv2.imread(record["maskPath"], cv2.IMREAD_GRAYSCALE)
        diff = np.abs(cs_prob-prediction)
        disagreement = np.count_nonzero(full != cs_mask)
        valid = ns["fix_circle"](np.ones((n,n)), size=n).astype(bool)
        binary_disagreement = np.count_nonzero(((cs_prob > threshold) != (prediction > threshold)) & valid)
        mae = float(diff.mean()); max_error = float(diff.max())
        fraction = float(binary_disagreement/valid.sum())
        # Gates concern port fidelity, not accuracy against labeled ground truth.
        passed = mae < 1e-4 and max_error < 0.01 and fraction < 0.001
        output = args.validation/"PythonReference"; output.mkdir(exist_ok=True)
        cv2.imwrite(str(output/(stem+".png")), full)
        item = {"model":variant, "size":n, "preprocessing_exact":True,
                "python_cpu_inference_seconds":elapsed, "probability_mae":mae,
                "probability_max_error":max_error, "disk_disagreement_fraction":fraction,
                "full_size_disagreeing_pixels":int(disagreement), "passed":passed}
        results.append(item); print(json.dumps(item), flush=True)
        del model
    (args.validation/"python-parity.json").write_text(json.dumps({"reference":str(args.original_inference),
        "torch":torch.__version__, "note":"Same detected disk supplied to original Python helpers. Does not validate automatic disk detection against calibration or ground-truth segmentation.",
        "results":results}, indent=2))
    if not all(r["passed"] for r in results): raise SystemExit(1)


if __name__ == "__main__": main()
