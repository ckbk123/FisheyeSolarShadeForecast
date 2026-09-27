function result = run_matlab_ocamcalib_reference(imageDir, toolboxDir, outputDir, squareSizeMm)
%RUN_MATLAB_OCAMCALIB_REFERENCE Exercise the original OCamCalib math.
%
% This is a validation adapter, not a redistributed copy of OCamCalib. It
% detects the board with the installed MATLAB Computer Vision Toolbox, feeds
% those observations to an external OCamCalib v3.0 checkout, refines all
% parameters with the toolbox residual, and writes reproducible metrics and
% overlays. Coordinates exported in result.json use conventional [x,y]
% image order. OCamCalib internally calls them [Yp_abs,Xp_abs].

arguments
    imageDir (1,1) string
    toolboxDir (1,1) string
    outputDir (1,1) string
    squareSizeMm (1,1) double {mustBePositive} = 22
end

if ~isfolder(imageDir)
    error("Calibration:MissingImages", "Image directory does not exist: %s", imageDir);
end
if ~isfile(fullfile(toolboxDir, "calibrate.m"))
    error("Calibration:MissingToolbox", "OCamCalib calibrate.m not found in: %s", toolboxDir);
end
if ~isfolder(outputDir)
    mkdir(outputDir);
end

addpath(toolboxDir);
cleanupPath = onCleanup(@() rmpath(toolboxDir)); %#ok<NASGU>

images = dir(fullfile(imageDir, "*.jpg"));
if isempty(images)
    error("Calibration:NoImages", "No JPEG calibration images found in: %s", imageDir);
end

numbers = nan(size(images));
for i = 1:numel(images)
    [~, stem] = fileparts(images(i).name);
    numbers(i) = str2double(stem);
end
if all(isfinite(numbers))
    [~, order] = sort(numbers);
else
    [~, order] = sort({images.name});
end
images = images(order);
imageFiles = fullfile({images.folder}, {images.name});

[imagePoints, boardSize, imagesUsed] = detectCheckerboardPoints(imageFiles);
imageFiles = imageFiles(imagesUsed);
imagePoints = imagePoints(:, :, imagesUsed);
if numel(imageFiles) < 3
    error("Calibration:TooFewBoards", "Only %d complete boards were detected.", numel(imageFiles));
end

% MATLAB boardSize counts squares surrounding the detected inner corners.
innerRows = boardSize(1) - 1;
innerColumns = boardSize(2) - 1;
worldXY = generateCheckerboardPoints(boardSize, squareSizeMm);

first = imfinfo(imageFiles{1});
rawWidth = first.Width;
rawHeight = first.Height;

% The original toolbox names the image row coordinate X and column Y.
XpAbs = permute(imagePoints(:, 2, :), [1 2 3]);
YpAbs = permute(imagePoints(:, 1, :), [1 2 3]);
% Its generated grid varies Y fastest, while MATLAB varies world X fastest.
Xt = worldXY(:, 2);
Yt = worldXY(:, 1);

imageIndices = 1:numel(imageFiles);
centerRow = (rawHeight + 1) / 2;
centerColumn = (rawWidth + 1) / 2;
polynomialDegree = 4;

[extrinsics, polynomial] = calibrate( ...
    Xt, Yt, XpAbs, YpAbs, centerRow, centerColumn, ...
    polynomialDegree, imageIndices);

initialModel = struct( ...
    "ss", polynomial, "xc", centerRow, "yc", centerColumn, ...
    "c", 1, "d", 0, "e", 0, ...
    "width", rawHeight, "height", rawWidth);
initialMetrics = calculateMetrics(initialModel, extrinsics, XpAbs, YpAbs, Xt, Yt);

[model, extrinsics, optimization] = refineAll( ...
    initialModel, extrinsics, imageIndices, XpAbs, YpAbs, Xt, Yt);
finalMetrics = calculateMetrics(model, extrinsics, XpAbs, YpAbs, Xt, Yt);

result = struct;
result.reference = "Scaramuzza OCamCalib v3.0 with MATLAB-detected corners";
result.matlab_version = version;
result.square_size_mm = squareSizeMm;
result.board_size_squares = boardSize;
result.inner_corners_rows_columns = [innerRows, innerColumns];
result.image_files = string(imageFiles);
result.raw_image_size_width_height = [rawWidth, rawHeight];
result.principal_point_xy = [model.yc, model.xc];
result.polynomial_radius_to_z = model.ss(:).';
result.affine_cde = [model.c, model.d, model.e];
result.initial_metrics = initialMetrics.summary;
result.final_metrics = finalMetrics.summary;
result.per_image = finalMetrics.perImage;
result.optimization = optimization;
result.extrinsics_ocam_layout = extrinsics;

save(fullfile(outputDir, "result.mat"), "result", "imagePoints", ...
    "worldXY", "initialMetrics", "finalMetrics", "model", "extrinsics");
writeText(fullfile(outputDir, "result.json"), jsonencode(result, PrettyPrint=true));
writeText(fullfile(outputDir, "detected_corners.json"), jsonencode(struct( ...
    "coordinate_order", "xy_one_based_raw_exif_orientation", ...
    "board_size_squares", boardSize, ...
    "square_size_mm", squareSizeMm, ...
    "image_files", string(imageFiles), ...
    "image_points", imagePoints), PrettyPrint=true));

for i = 1:numel(imageFiles)
    image = imread(imageFiles{i});
    observed = imagePoints(:, :, i);
    projected = squeeze(finalMetrics.projectedXY(:, :, i));
    overlay = insertMarker(image, observed, "+", Color="green", Size=8);
    overlay = insertMarker(overlay, projected, "x", Color="red", Size=8);
    overlay = insertShape(overlay, "Line", ...
        [observed, projected], Color="yellow", LineWidth=1);
    [~, stem] = fileparts(imageFiles{i});
    imwrite(overlay, fullfile(outputDir, "overlay_" + stem + ".jpg"), Quality=94);
end

fprintf("OCamCalib reference complete: %d/%d images, %dx%d inner corners.\n", ...
    numel(imageFiles), numel(images), innerRows, innerColumns);
fprintf("Initial 2-D RMSE %.6f px; final 2-D RMSE %.6f px; final mean %.6f px; p95 %.6f px.\n", ...
    initialMetrics.summary.rmse_2d_px, finalMetrics.summary.rmse_2d_px, ...
    finalMetrics.summary.mean_euclidean_px, finalMetrics.summary.p95_euclidean_px);
end

function [model, extrinsicsOut, optimization] = refineAll( ...
    model, extrinsics, imageIndices, XpAbs, YpAbs, Xt, Yt)
world = [Xt, Yt, zeros(size(Xt))];
state = [model.c; model.d; model.e];
for i = imageIndices
    r1 = extrinsics(:, 1, i);
    r2 = extrinsics(:, 2, i);
    rotationVector = rodrigues([r1, r2, cross(r1, r2)]);
    translation = extrinsics(:, 3, i);
    state = [state; rotationVector; translation]; %#ok<AGROW>
end
state = [state; model.xc; model.yc; ones(size(model.ss))];

% Match optimizefunction_all.m from OCamCalib v3.0. The original passes
% these legacy optimset values to lsqnonlin's trust-region-reflective
% implementation; only its interactive display and ineffective dense
% JacobPattern string are omitted here.
numberOfVariables = numel(state);
options = optimset( ...
    "Display", "off", "LargeScale", "off", ...
    "TolX", 1e-4, "TolFun", 1e-4, ...
    "DerivativeCheck", "off", "Diagnostics", "off", ...
    "Jacobian", "off", "JacobMult", [], ...
    "MaxFunEvals", 100 * numberOfVariables, ...
    "DiffMaxChange", 1e-1, "DiffMinChange", 1e-8, ...
    "PrecondBandWidth", 0, "TypicalX", ones(numberOfVariables, 1), ...
    "MaxPCGIter", max(1, floor(numberOfVariables / 2)), ...
    "TolPCG", 0.1, "MaxIter", 10000, ...
    "Algorithm", "trust-region-reflective");
started = tic;
[solution, resnorm, residual, exitflag, output] = lsqnonlin( ...
    @prova_all, state, [], [], options, model.ss, imageIndices, ...
    XpAbs, YpAbs, world, model.width, model.height);

nPolynomial = numel(model.ss);
model.xc = solution(end - nPolynomial - 1);
model.yc = solution(end - nPolynomial);
model.c = solution(1);
model.d = solution(2);
model.e = solution(3);
model.ss = model.ss .* solution(end - nPolynomial + 1:end);

extrinsicsOut = zeros(size(extrinsics));
counter = 0;
for i = imageIndices
    rotation = rodrigues(solution(6 * counter + 4:6 * counter + 6));
    translation = solution(6 * counter + 7:6 * counter + 9);
    extrinsicsOut(:, :, i) = rotation;
    extrinsicsOut(:, 3, i) = translation;
    counter = counter + 1;
end

optimization = struct( ...
    "success", exitflag > 0, "exit_flag", exitflag, ...
    "resnorm", resnorm, "iterations", output.iterations, ...
    "function_count", output.funcCount, "elapsed_seconds", toc(started), ...
    "residual_count", numel(residual));
end

function metrics = calculateMetrics(model, extrinsics, XpAbs, YpAbs, Xt, Yt)
nImages = size(XpAbs, 3);
nPoints = size(XpAbs, 1);
projectedXY = zeros(nPoints, 2, nImages);
errors = zeros(nPoints, nImages);
dx = zeros(nPoints, nImages);
dy = zeros(nPoints, nImages);
worldHomogeneous = [Xt, Yt, ones(size(Xt))].';

for i = 1:nImages
    cameraPoints = extrinsics(:, :, i) * worldHomogeneous;
    [rowIdeal, columnIdeal] = omni3d2pixel( ...
        model.ss, cameraPoints, model.width, model.height);
    row = rowIdeal * model.c + columnIdeal * model.d + model.xc;
    column = rowIdeal * model.e + columnIdeal + model.yc;
    projectedXY(:, :, i) = [column(:), row(:)];
    dx(:, i) = column(:) - YpAbs(:, :, i);
    dy(:, i) = row(:) - XpAbs(:, :, i);
    errors(:, i) = hypot(dx(:, i), dy(:, i));
end

flat = errors(:);
metrics = struct;
metrics.summary = struct( ...
    "rmse_2d_px", sqrt(mean(dx(:).^2 + dy(:).^2)), ...
    "rmse_per_coordinate_px", sqrt(mean([dx(:); dy(:)].^2)), ...
    "mean_euclidean_px", mean(flat), ...
    "median_euclidean_px", median(flat), ...
    "p95_euclidean_px", prctile(flat, 95), ...
    "max_euclidean_px", max(flat), ...
    "point_count", numel(flat));
metrics.perImage = struct( ...
    "mean_euclidean_px", num2cell(mean(errors, 1)), ...
    "rmse_2d_px", num2cell(sqrt(mean(errors.^2, 1))), ...
    "max_euclidean_px", num2cell(max(errors, [], 1)));
metrics.projectedXY = projectedXY;
metrics.dx = dx;
metrics.dy = dy;
metrics.euclidean = errors;
end

function writeText(path, text)
file = fopen(path, "w", "n", "UTF-8");
if file < 0
    error("Calibration:WriteFailed", "Cannot write %s", path);
end
cleanupFile = onCleanup(@() fclose(file)); %#ok<NASGU>
fwrite(file, text, "char");
end
