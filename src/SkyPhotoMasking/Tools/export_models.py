"""Development-only export; never needed by the C# application. No training/downloads."""
import argparse
import hashlib
import json
import time
from pathlib import Path

import cv2
import numpy as np
import onnx
import segmentation_models_pytorch as smp
import torch


def load_model(path, variant):
    model = smp.UnetPlusPlus(encoder_name=f"efficientnet-{variant}",
                            encoder_weights=None, in_channels=3, classes=1, activation="sigmoid")
    checkpoint = torch.load(path, map_location="cpu", weights_only=True)
    model.load_state_dict(checkpoint["model_state_dict"], strict=True)
    model.encoder.set_swish(memory_efficient=False)
    return model.eval()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("weights", type=Path)
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parents[1] / "Models")
    parser.add_argument("--model", choices=["b4", "b5", "b6", "b7"])
    args = parser.parse_args()
    torch.set_num_threads(8)
    args.output.mkdir(parents=True, exist_ok=True)
    for variant in ([args.model] if args.model else ["b4", "b5", "b6", "b7"]):
        start = time.perf_counter()
        source = args.weights / f"efficientnet-{variant}.pt"
        model = load_model(source, variant)
        output = args.output / f"efficientnet-{variant}.onnx"
        # Dynamic axes are specialized to 512 or 1024 by the C# session options.
        # Eval disables dropout; sigmoid and BatchNorm running statistics are preserved.
        with torch.inference_mode():
            torch.onnx.export(model, torch.zeros(1, 3, 512, 512), str(output),
                              opset_version=17, input_names=["image"], output_names=["obstacle_probability"],
                              dynamic_axes={"image": {2: "height", 3: "width"},
                                            "obstacle_probability": {2: "height", 3: "width"}},
                              do_constant_folding=True, dynamo=False)
        graph = onnx.load(str(output))
        onnx.checker.check_model(graph)
        threshold = {"b4":155, "b5":158, "b6":148, "b7":161}[variant] / 255
        metadata = {"variant": variant, "architecture": "UnetPlusPlus", "encoder": f"efficientnet-{variant}",
                    "threshold": str(threshold), "input": "RGB float32 NCHW /255", "output": "obstacle_probability",
                    "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest()}
        onnx.helper.set_model_props(graph, metadata)
        onnx.save(graph, str(output))
        metadata.update(onnx_sha256=hashlib.sha256(output.read_bytes()).hexdigest(),
                        bytes=output.stat().st_size, torch=torch.__version__, smp=smp.__version__,
                        onnx=onnx.__version__, export_seconds=time.perf_counter()-start)
        output.with_suffix(".json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
        print(json.dumps(metadata), flush=True)
        del graph, model


if __name__ == "__main__":
    main()
