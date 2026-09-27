"""Run the MIT-licensed py-omnicalib algorithm as a reproducible oracle.

This adapter deliberately separates corner observations from calibration. It
writes both to JSON so the same observations can later be consumed by MATLAB
and C# without conflating detector differences with solver differences.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib
import json
import platform
import sys
from dataclasses import asdict, dataclass
from pathlib import Path

import cv2
import numpy as np
import torch
import yaml


@dataclass(frozen=True)
class ErrorSummary:
    rmse_2d_px: float
    rmse_per_coordinate_px: float
    mean_euclidean_px: float
    median_euclidean_px: float
    p95_euclidean_px: float
    max_euclidean_px: float
    point_count: int


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def natural_image_paths(image_dir: Path) -> list[Path]:
    paths = [path for path in image_dir.iterdir() if path.suffix.lower() in {".jpg", ".jpeg", ".png"}]

    def key(path: Path) -> tuple[int, int | str]:
        return (0, int(path.stem)) if path.stem.isdigit() else (1, path.name.lower())

    return sorted(paths, key=key)


def sharpen(gray: np.ndarray) -> np.ndarray:
    kernel = np.array([[0, -1, 0], [-1, 5, -1], [0, -1, 0]], dtype=np.float32)
    return cv2.filter2D(gray, -1, kernel)


def detect_corners(
    paths: list[Path], inner_columns: int, inner_rows: int, downsample: int, epsilon: float
) -> tuple[list[Path], np.ndarray]:
    pattern = (inner_columns, inner_rows)
    accepted_paths: list[Path] = []
    all_corners: list[np.ndarray] = []
    cv2.setNumThreads(1)

    for path in paths:
        gray = cv2.imread(str(path), cv2.IMREAD_GRAYSCALE)
        if gray is None:
            continue
        gray = sharpen(gray)
        small = cv2.resize(
            gray,
            (gray.shape[1] // downsample, gray.shape[0] // downsample),
            interpolation=cv2.INTER_AREA,
        )
        small = sharpen(small)
        found, corners = cv2.findChessboardCorners(
            small,
            pattern,
            cv2.CALIB_CB_ADAPTIVE_THRESH | cv2.CALIB_CB_NORMALIZE_IMAGE,
        )
        if not found:
            found, corners = cv2.findChessboardCornersSB(small, pattern, cv2.CALIB_CB_EXHAUSTIVE)
        if not found:
            continue

        scale = np.array([gray.shape[1] / small.shape[1], gray.shape[0] / small.shape[0]], dtype=np.float32)
        corners = corners.reshape(-1, 2) * scale
        corners = cv2.cornerSubPix(
            gray,
            corners.reshape(-1, 1, 2),
            (11, 11),
            (-1, -1),
            (cv2.TERM_CRITERIA_EPS | cv2.TERM_CRITERIA_MAX_ITER, 50, epsilon),
        ).reshape(-1, 2)
        accepted_paths.append(path)
        all_corners.append(corners.astype(np.float64))

    if not all_corners:
        raise RuntimeError("No complete checkerboards were detected.")
    return accepted_paths, np.stack(all_corners)


def checkerboard_points(inner_columns: int, inner_rows: int, square_size_mm: float) -> np.ndarray:
    x, y = np.meshgrid(np.arange(inner_columns), np.arange(inner_rows), indexing="xy")
    return np.column_stack((x.ravel(), y.ravel(), np.zeros(x.size))) * square_size_mm


def summarize(projected: np.ndarray, observed: np.ndarray) -> tuple[ErrorSummary, np.ndarray]:
    delta = projected - observed
    euclidean = np.linalg.norm(delta, axis=-1)
    summary = ErrorSummary(
        rmse_2d_px=float(np.sqrt(np.mean(np.sum(delta * delta, axis=-1)))),
        rmse_per_coordinate_px=float(np.sqrt(np.mean(delta * delta))),
        mean_euclidean_px=float(np.mean(euclidean)),
        median_euclidean_px=float(np.median(euclidean)),
        p95_euclidean_px=float(np.percentile(euclidean, 95)),
        max_euclidean_px=float(np.max(euclidean)),
        point_count=int(euclidean.size),
    )
    return summary, euclidean


def distribution(values: np.ndarray) -> dict[str, float]:
    values = np.asarray(values).reshape(-1)
    return {
        "min": float(np.min(values)),
        "median": float(np.median(values)),
        "p95": float(np.percentile(values, 95)),
        "max": float(np.max(values)),
    }


def write_overlays(
    output_dir: Path,
    paths: list[Path],
    observed: np.ndarray,
    projected: np.ndarray,
    euclidean: np.ndarray,
) -> None:
    for index, path in enumerate(paths):
        image = cv2.imread(str(path), cv2.IMREAD_COLOR)
        if image is None:
            continue
        for actual, fitted, error in zip(observed[index], projected[index], euclidean[index]):
            actual_point = tuple(np.rint(actual).astype(int))
            fitted_point = tuple(np.rint(fitted).astype(int))
            cv2.drawMarker(image, actual_point, (0, 255, 0), cv2.MARKER_CROSS, 18, 2)
            cv2.drawMarker(image, fitted_point, (0, 0, 255), cv2.MARKER_TILTED_CROSS, 18, 2)
            color = (0, 255, 255) if error <= 2 else (0, 128, 255)
            cv2.line(image, actual_point, fitted_point, color, 1, cv2.LINE_AA)
        cv2.imwrite(str(output_dir / f"overlay_{path.stem}.jpg"), image, [cv2.IMWRITE_JPEG_QUALITY, 94])


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("image_dir", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--inner-columns", type=int, default=6)
    parser.add_argument("--inner-rows", type=int, default=9)
    parser.add_argument("--square-size-mm", type=float, default=22.0)
    parser.add_argument("--downsample", type=int, default=8)
    parser.add_argument("--corner-epsilon", type=float, default=0.001)
    parser.add_argument("--minimum-images", type=int, default=8)
    parser.add_argument("--skip-overlays", action="store_true")
    parser.add_argument(
        "--observations-json",
        type=Path,
        help="Use frozen observations instead of running the Python detector.",
    )
    args = parser.parse_args()

    args.output_dir.mkdir(parents=True, exist_ok=True)
    paths = natural_image_paths(args.image_dir)
    if args.observations_json:
        frozen = json.loads(args.observations_json.read_text(encoding="utf-8"))
        accepted = [Path(item["path"]) for item in frozen["images"]]
        observed = np.asarray([item["points_xy"] for item in frozen["images"]], dtype=np.float64)
        args.inner_columns, args.inner_rows = frozen["inner_corners_columns_rows"]
        args.square_size_mm = frozen["square_size_mm"]
    else:
        accepted, observed = detect_corners(
            paths, args.inner_columns, args.inner_rows, args.downsample, args.corner_epsilon
        )
    if len(accepted) < args.minimum_images:
        raise RuntimeError(f"Only {len(accepted)} complete checkerboards were detected.")

    world = checkerboard_points(args.inner_columns, args.inner_rows, args.square_size_mm)
    world_batch = np.repeat(world[None, :, :], len(accepted), axis=0)
    image = cv2.imread(str(accepted[0]), cv2.IMREAD_GRAYSCALE)
    assert image is not None
    height, width = image.shape

    calibration_module = importlib.import_module("omnicalib.calibrate")
    geometry_module = importlib.import_module("omnicalib.geometry")
    projection_module = importlib.import_module("omnicalib.projection")
    calibration_module.show_points = lambda *unused_args, **unused_kwargs: None

    observed_tensor = torch.from_numpy(observed).to(torch.float64)
    world_tensor = torch.from_numpy(world_batch).to(torch.float64)
    principal_initial = observed_tensor.new_tensor((width, height)) * 0.5 - 0.5
    rotations, translations, poly_theta, poly_rz, principal = calibration_module.calibrate(
        4,
        100.0,
        max(1, round(len(accepted) / 4)),
        observed_tensor,
        world_tensor,
        principal_initial,
        images=None,
        image_shape=(height, width),
    )
    view = geometry_module.transform(world_tensor, rotations, translations)
    projected_tensor = projection_module.project_poly_thetar(view, poly_theta, principal)
    projected = projected_tensor.detach().cpu().numpy()
    summary, euclidean = summarize(projected, observed)
    principal_numpy = principal.detach().cpu().numpy()
    corner_radius = np.linalg.norm(observed - principal_numpy, axis=-1)
    view_numpy = view.detach().cpu().numpy()
    incident_degrees = np.rad2deg(
        np.arccos(np.clip(view_numpy[..., 2] / np.linalg.norm(view_numpy, axis=-1), -1.0, 1.0))
    )
    poly_theta_numpy = poly_theta.detach().cpu().numpy()
    derivative_roots = np.polynomial.polynomial.polyroots(
        np.arange(1, len(poly_theta_numpy)) * poly_theta_numpy[1:]
    )
    turning_points = sorted(
        float(np.rad2deg(root.real))
        for root in derivative_roots
        if abs(root.imag) < 1e-9 and 0 < root.real < np.pi / 2
    )

    per_image = []
    for index, path in enumerate(accepted):
        per_summary, _ = summarize(projected[index], observed[index])
        per_image.append({"image": path.name, **asdict(per_summary)})

    observations = {
        "schema_version": 1,
        "coordinate_order": "xy_zero_based_after_exif_orientation",
        "image_size_width_height": [width, height],
        "inner_corners_columns_rows": [args.inner_columns, args.inner_rows],
        "square_size_mm": args.square_size_mm,
        "detector": {
            "opencv_version": cv2.__version__,
            "downsample": args.downsample,
            "corner_subpix_epsilon": args.corner_epsilon,
            "sequence": (
                f"frozen observations from {args.observations_json.resolve()}"
                if args.observations_json
                else "sharpen, downsample, sharpen, classic, SB fallback, full-resolution subpixel"
            ),
        },
        "images": [
            {
                "path": str(path.resolve()),
                "name": path.name,
                "sha256": file_sha256(path),
                "points_xy": observed[index].tolist(),
            }
            for index, path in enumerate(accepted)
        ],
        "object_points_xyz": world.tolist(),
    }
    result = {
        "schema_version": 1,
        "reference": "Thomas Poenitz py-omnicalib, MIT licence",
        "runtime": {
            "python": platform.python_version(),
            "numpy": np.__version__,
            "torch": torch.__version__,
            "opencv": cv2.__version__,
        },
        "image_size_width_height": [width, height],
        "square_size_mm": args.square_size_mm,
        "inner_corners_columns_rows": [args.inner_columns, args.inner_rows],
        "principal_point_xy": principal.detach().cpu().tolist(),
        "polynomial_incident_angle_to_radius": poly_theta.detach().cpu().tolist(),
        "polynomial_radius_to_z": poly_rz.detach().cpu().tolist(),
        "extrinsics": [
            torch.cat((rotation, translation[:, None]), dim=1).detach().cpu().tolist()
            for rotation, translation in zip(rotations, translations)
        ],
        "calibration_coverage": {
            "observed_corner_radius_px": distribution(corner_radius),
            "fitted_incident_angle_degrees": distribution(incident_degrees),
            "polynomial_turning_points_0_to_90_degrees": turning_points,
            "supports_full_sky_hemisphere": bool(np.max(incident_degrees) >= 85.0),
        },
        "metrics": asdict(summary),
        "per_image": per_image,
    }

    (args.output_dir / "observations.json").write_text(
        json.dumps(observations, indent=2), encoding="utf-8"
    )
    (args.output_dir / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    (args.output_dir / "calibration.yml").write_text(
        yaml.safe_dump(
            {
                "extrinsics": result["extrinsics"],
                "poly_incident_angle_to_radius": result["polynomial_incident_angle_to_radius"],
                "poly_radius_to_z": result["polynomial_radius_to_z"],
                "principal_point": result["principal_point_xy"],
                # Legacy consumers call this `fov`, but it is a half-angle:
                # the largest incident angle actually constrained by corners.
                "fov": result["calibration_coverage"]["fitted_incident_angle_degrees"]["max"],
            },
            sort_keys=False,
        ),
        encoding="utf-8",
    )
    if not args.skip_overlays:
        write_overlays(args.output_dir, accepted, observed, projected, euclidean)
    print(json.dumps({"detected": len(accepted), "available": len(paths), "metrics": asdict(summary)}, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
