const ROOM_BOUNDS = Object.freeze({
  minX: -3.4,
  maxX: 3.4,
  minZ: -2.45,
  maxZ: 2.45,
});

const VIEWS = Object.freeze([
  { position: [-6.8, 5.6, -7.4], target: [0, 0.85, 0.15], focal: 1.79 },
  { position: [6.8, 5.4, -7.25], target: [0, 0.82, 0.1], focal: 1.79 },
  { position: [0, 8.55, -9.8], target: [0, 0.62, 0.1], focal: 1.84 },
]);

const COLORS = Object.freeze({
  bone: "#e9dfca",
  boneShade: "#bdb098",
  brass: "#a98551",
  brassLight: "#d1ad6f",
  floor: "#241c16",
  wall: "#1b1511",
  wallDeep: "#100d0b",
  underside: "#0b0908",
});

const clamp = (value, min = 0, max = 1) => Math.min(max, Math.max(min, value));
const mix = (a, b, amount) => a + (b - a) * amount;
const point = (x, y, z) => ({ x, y, z });

function now() {
  return typeof performance === "undefined" ? Date.now() : performance.now();
}

function subtract(a, b) {
  return point(a.x - b.x, a.y - b.y, a.z - b.z);
}

function cross(a, b) {
  return point(
    a.y * b.z - a.z * b.y,
    a.z * b.x - a.x * b.z,
    a.x * b.y - a.y * b.x,
  );
}

function dot(a, b) {
  return a.x * b.x + a.y * b.y + a.z * b.z;
}

function normalize(value) {
  const length = Math.hypot(value.x, value.y, value.z) || 1;
  return point(value.x / length, value.y / length, value.z / length);
}

function cameraFrame(view, width, height, variant) {
  const position = point(...view.position);
  const target = point(...view.target);
  const forward = normalize(subtract(target, position));
  const right = normalize(cross(forward, point(0, 1, 0)));
  const up = normalize(cross(right, forward));
  const compact = variant === "hero" && width < height * 0.9;
  const centerY = height * (compact ? 0.54 : 0.52);

  const frame = {
    position,
    forward,
    right,
    up,
    focal: Math.min(width, height) * view.focal,
    centerX: width * 0.5,
    centerY,
  };

  if (variant !== "hero" && width < 600) {
    const roomCorners = [];
    for (const x of [ROOM_BOUNDS.minX, ROOM_BOUNDS.maxX]) {
      for (const z of [ROOM_BOUNDS.minZ, ROOM_BOUNDS.maxZ]) {
        roomCorners.push(point(x, 0, z), point(x, 3.05, z));
      }
    }
    const initialBounds = boundsOf(projected(frame, roomCorners));
    const boundsWidth = Math.max(1, initialBounds.maxX - initialBounds.minX);
    const boundsHeight = Math.max(1, initialBounds.maxY - initialBounds.minY);
    const fit = Math.min(1, (width * 0.84) / boundsWidth, (height * 0.84) / boundsHeight);
    frame.focal *= fit;

    const fittedBounds = boundsOf(projected(frame, roomCorners));
    frame.centerX += width * 0.5 - (fittedBounds.minX + fittedBounds.maxX) * 0.5;
    frame.centerY += height * 0.5 - (fittedBounds.minY + fittedBounds.maxY) * 0.5;
  }

  return frame;
}

function project(worldPoint, camera) {
  const relative = subtract(worldPoint, camera.position);
  const depth = Math.max(0.01, dot(relative, camera.forward));
  const scale = camera.focal / depth;
  return {
    x: camera.centerX + dot(relative, camera.right) * scale,
    y: camera.centerY - dot(relative, camera.up) * scale,
    depth,
    scale,
  };
}

function projected(camera, points) {
  return points.map((value) => project(value, camera));
}

function pathPolygon(context, points) {
  if (!points.length) return;
  context.beginPath();
  context.moveTo(points[0].x, points[0].y);
  for (let index = 1; index < points.length; index += 1) {
    context.lineTo(points[index].x, points[index].y);
  }
  context.closePath();
}

function strokeWorldLine(context, camera, a, b, color, width = 1) {
  const start = project(a, camera);
  const end = project(b, camera);
  context.beginPath();
  context.moveTo(start.x, start.y);
  context.lineTo(end.x, end.y);
  context.strokeStyle = color;
  context.lineWidth = width;
  context.stroke();
}

function boundsOf(points) {
  return points.reduce(
    (bounds, value) => ({
      minX: Math.min(bounds.minX, value.x),
      maxX: Math.max(bounds.maxX, value.x),
      minY: Math.min(bounds.minY, value.y),
      maxY: Math.max(bounds.maxY, value.y),
    }),
    { minX: Infinity, maxX: -Infinity, minY: Infinity, maxY: -Infinity },
  );
}

function fillProjectedFace(context, camera, worldPoints, fill, stroke = null, width = 1) {
  const points = projected(camera, worldPoints);
  pathPolygon(context, points);
  context.fillStyle = typeof fill === "function" ? fill(boundsOf(points)) : fill;
  context.fill();
  if (stroke) {
    context.strokeStyle = stroke;
    context.lineWidth = width;
    context.stroke();
  }
  return points;
}

function drawImageInQuad(context, image, quad, alpha) {
  if (!image || !image.naturalWidth || alpha <= 0) return;

  const [topLeft, topRight, , bottomLeft] = quad;
  context.save();
  pathPolygon(context, quad);
  context.clip();
  context.globalAlpha *= alpha;
  context.transform(
    (topRight.x - topLeft.x) / image.naturalWidth,
    (topRight.y - topLeft.y) / image.naturalWidth,
    (bottomLeft.x - topLeft.x) / image.naturalHeight,
    (bottomLeft.y - topLeft.y) / image.naturalHeight,
    topLeft.x,
    topLeft.y,
  );
  context.drawImage(image, 0, 0);
  context.restore();
}

function drawFloorShadow(context, camera) {
  const shadow = projected(camera, [
    point(-3.65, -0.42, -2.7),
    point(3.65, -0.42, -2.7),
    point(3.65, -0.42, 2.7),
    point(-3.65, -0.42, 2.7),
  ]);
  context.save();
  context.shadowColor = "rgba(0, 0, 0, 0.82)";
  context.shadowBlur = 30;
  context.shadowOffsetY = 18;
  pathPolygon(context, shadow);
  context.fillStyle = "rgba(0, 0, 0, 0.68)";
  context.fill();
  context.restore();
}

function drawArchitecture(context, camera, roomImage, progress) {
  drawFloorShadow(context, camera);

  const floorFront = [
    point(-3.4, 0, -2.45),
    point(3.4, 0, -2.45),
    point(3.4, -0.2, -2.45),
    point(-3.4, -0.2, -2.45),
  ];
  const floorRight = [
    point(3.4, 0, -2.45),
    point(3.4, 0, 2.45),
    point(3.4, -0.2, 2.45),
    point(3.4, -0.2, -2.45),
  ];
  const floorLeft = [
    point(-3.4, 0, 2.45),
    point(-3.4, 0, -2.45),
    point(-3.4, -0.2, -2.45),
    point(-3.4, -0.2, 2.45),
  ];

  for (const face of [floorLeft, floorRight, floorFront]) {
    fillProjectedFace(context, camera, face, COLORS.underside, "rgba(169, 133, 81, 0.28)", 0.8);
  }

  const backWall = [
    point(-3.4, 0, 2.45),
    point(3.4, 0, 2.45),
    point(3.4, 3.05, 2.45),
    point(-3.4, 3.05, 2.45),
  ];
  const leftWall = [
    point(-3.4, 0, -2.45),
    point(-3.4, 0, 2.45),
    point(-3.4, 3.05, 2.45),
    point(-3.4, 2.28, -2.45),
  ];

  fillProjectedFace(context, camera, backWall, (bounds) => {
    const gradient = context.createLinearGradient(0, bounds.minY, 0, bounds.maxY);
    gradient.addColorStop(0, "#211914");
    gradient.addColorStop(0.58, COLORS.wall);
    gradient.addColorStop(1, COLORS.wallDeep);
    return gradient;
  });

  fillProjectedFace(context, camera, leftWall, (bounds) => {
    const gradient = context.createLinearGradient(bounds.minX, 0, bounds.maxX, 0);
    gradient.addColorStop(0, "#0f0c0a");
    gradient.addColorStop(1, "#211813");
    return gradient;
  });

  const photoPanel = projected(camera, [
    point(-2.42, 0.58, 2.43),
    point(2.42, 0.58, 2.43),
    point(2.42, 2.62, 2.43),
    point(-2.42, 2.62, 2.43),
  ]);
  drawImageInQuad(context, roomImage, photoPanel, 0.11 + progress * 0.055);
  pathPolygon(context, photoPanel);
  context.fillStyle = `rgba(12, 9, 7, ${0.64 - progress * 0.05})`;
  context.fill();
  context.strokeStyle = "rgba(198, 158, 97, 0.3)";
  context.lineWidth = 0.8;
  context.stroke();

  const floor = [
    point(-3.4, 0, -2.45),
    point(3.4, 0, -2.45),
    point(3.4, 0, 2.45),
    point(-3.4, 0, 2.45),
  ];
  const floorScreen = fillProjectedFace(context, camera, floor, (bounds) => {
    const lightX = mix(bounds.minX, bounds.maxX, 0.48 + progress * 0.035);
    const lightY = mix(bounds.minY, bounds.maxY, 0.44);
    const radius = Math.max(bounds.maxX - bounds.minX, bounds.maxY - bounds.minY) * 0.68;
    const gradient = context.createRadialGradient(lightX, lightY, 2, lightX, lightY, radius);
    gradient.addColorStop(0, "#33271d");
    gradient.addColorStop(0.38, COLORS.floor);
    gradient.addColorStop(1, "#130f0c");
    return gradient;
  });

  context.save();
  pathPolygon(context, floorScreen);
  context.clip();
  const poolCenter = project(point(mix(-0.22, 0.42, progress), 0.01, 0.18), camera);
  const floorBounds = boundsOf(floorScreen);
  const poolRadius = Math.max(
    floorBounds.maxX - floorBounds.minX,
    floorBounds.maxY - floorBounds.minY,
  ) * 0.31;
  const pool = context.createRadialGradient(
    poolCenter.x,
    poolCenter.y,
    0,
    poolCenter.x,
    poolCenter.y,
    poolRadius,
  );
  pool.addColorStop(0, `rgba(196, 153, 91, ${0.105 + progress * 0.035})`);
  pool.addColorStop(0.5, "rgba(151, 111, 66, 0.045)");
  pool.addColorStop(1, "rgba(0, 0, 0, 0)");
  context.fillStyle = pool;
  context.fillRect(
    floorBounds.minX,
    floorBounds.minY,
    floorBounds.maxX - floorBounds.minX,
    floorBounds.maxY - floorBounds.minY,
  );
  context.restore();

  for (let x = -3; x <= 3.001; x += 1) {
    strokeWorldLine(
      context,
      camera,
      point(x, 0.015, -2.45),
      point(x, 0.015, 2.45),
      "rgba(185, 148, 94, 0.07)",
      0.55,
    );
  }
  for (let z = -2; z <= 2.001; z += 1) {
    strokeWorldLine(
      context,
      camera,
      point(-3.4, 0.015, z),
      point(3.4, 0.015, z),
      "rgba(185, 148, 94, 0.07)",
      0.55,
    );
  }

  const platformTop = [
    point(-1.1, 0.12, 1.42),
    point(1.1, 0.12, 1.42),
    point(1.1, 0.12, 2.18),
    point(-1.1, 0.12, 2.18),
  ];
  const platformFront = [
    point(-1.1, 0, 1.42),
    point(1.1, 0, 1.42),
    point(1.1, 0.12, 1.42),
    point(-1.1, 0.12, 1.42),
  ];
  fillProjectedFace(context, camera, platformFront, "rgba(11, 9, 8, 0.94)");
  fillProjectedFace(context, camera, platformTop, "rgba(48, 36, 27, 0.94)", "rgba(196, 155, 94, 0.22)", 0.7);

  const frameAlpha = 0.27 + progress * 0.1;
  const frameColor = `rgba(196, 155, 94, ${frameAlpha})`;
  const corners = [
    point(-3.4, 0, -2.45),
    point(3.4, 0, -2.45),
    point(3.4, 0, 2.45),
    point(-3.4, 0, 2.45),
  ];
  for (let index = 0; index < corners.length; index += 1) {
    strokeWorldLine(context, camera, corners[index], corners[(index + 1) % corners.length], frameColor, 0.8);
  }
  for (const corner of [corners[0], corners[2], corners[3]]) {
    strokeWorldLine(
      context,
      camera,
      corner,
      point(corner.x, corner.z < 0 ? 2.28 : 3.05, corner.z),
      "rgba(204, 165, 104, 0.27)",
      0.7,
    );
  }
  strokeWorldLine(
    context,
    camera,
    point(-3.4, 3.05, 2.45),
    point(3.4, 3.05, 2.45),
    "rgba(215, 176, 111, 0.32)",
    0.8,
  );

  const coneApex = project(point(-0.25, 3.55, 1.15), camera);
  const coneLeft = project(point(-1.24, 0.03, 0.1), camera);
  const coneRight = project(point(0.98, 0.03, 0.1), camera);
  const cone = context.createLinearGradient(coneApex.x, coneApex.y, (coneLeft.x + coneRight.x) * 0.5, (coneLeft.y + coneRight.y) * 0.5);
  cone.addColorStop(0, "rgba(210, 174, 116, 0.048)");
  cone.addColorStop(1, "rgba(205, 163, 98, 0.004)");
  context.beginPath();
  context.moveTo(coneApex.x, coneApex.y);
  context.lineTo(coneRight.x, coneRight.y);
  context.lineTo(coneLeft.x, coneLeft.y);
  context.closePath();
  context.fillStyle = cone;
  context.fill();
}

function sampleToNormalized(sample) {
  return {
    x: clamp((sample.x + 2.55) / 5.1),
    y: clamp((1.78 - sample.z) / 3.56),
  };
}

function drawImageCover(context, image, width, height) {
  if (!image?.naturalWidth || !image?.naturalHeight) return false;
  const scale = Math.max(width / image.naturalWidth, height / image.naturalHeight);
  const sourceWidth = width / scale;
  const sourceHeight = height / scale;
  const sourceX = (image.naturalWidth - sourceWidth) * 0.5;
  const sourceY = (image.naturalHeight - sourceHeight) * 0.5;
  context.drawImage(image, sourceX, sourceY, sourceWidth, sourceHeight, 0, 0, width, height);
  return true;
}

function drawHeroScene(context, image, width, height, current, observed, reducedMotion) {
  if (!drawImageCover(context, image, width, height)) {
    const fallback = context.createLinearGradient(0, 0, width, height);
    fallback.addColorStop(0, "#211914");
    fallback.addColorStop(0.55, "#15110e");
    fallback.addColorStop(1, "#0d0b09");
    context.fillStyle = fallback;
    context.fillRect(0, 0, width, height);
  }

  context.save();
  context.globalCompositeOperation = "screen";
  context.fillStyle = "rgba(112, 79, 48, 0.1)";
  context.fillRect(0, 0, width, height);
  context.restore();

  const vignette = context.createRadialGradient(
    width * 0.5,
    height * 0.46,
    Math.min(width, height) * 0.18,
    width * 0.5,
    height * 0.48,
    Math.max(width, height) * 0.72,
  );
  vignette.addColorStop(0, "rgba(0, 0, 0, 0)");
  vignette.addColorStop(0.74, "rgba(0, 0, 0, 0.025)");
  vignette.addColorStop(1, "rgba(0, 0, 0, 0.24)");
  context.fillStyle = vignette;
  context.fillRect(0, 0, width, height);

  if (reducedMotion) return;

  const currentPosition = sampleToNormalized(current);
  const observedPosition = sampleToNormalized(observed);
  const frameWidth = Math.min(width - 24, Math.max(110, width * 0.24));
  const frameHeight = frameWidth * 0.565;
  const margin = Math.max(12, Math.min(width, height) * 0.026);
  const place = (position) => ({
    x: mix(frameWidth * 0.5 + margin, width - frameWidth * 0.5 - margin, position.x),
    y: mix(frameHeight * 0.5 + margin, height - frameHeight * 0.5 - margin, position.y),
  });
  const currentPoint = place(currentPosition);
  const frameCenter = place(observedPosition);
  const frameX = frameCenter.x - frameWidth * 0.5;
  const frameY = frameCenter.y - frameHeight * 0.5;

  context.save();
  context.beginPath();
  context.rect(frameX, frameY, frameWidth, frameHeight);
  context.clip();
  if (image?.naturalWidth && image?.naturalHeight) {
    const aspect = frameWidth / frameHeight;
    let sourceWidth = image.naturalWidth / 1.3;
    let sourceHeight = sourceWidth / aspect;
    if (sourceHeight > image.naturalHeight / 1.3) {
      sourceHeight = image.naturalHeight / 1.3;
      sourceWidth = sourceHeight * aspect;
    }
    const sourceX = clamp(
      observedPosition.x * image.naturalWidth - sourceWidth * 0.5,
      0,
      image.naturalWidth - sourceWidth,
    );
    const sourceY = clamp(
      observedPosition.y * image.naturalHeight - sourceHeight * 0.5,
      0,
      image.naturalHeight - sourceHeight,
    );
    context.drawImage(
      image,
      sourceX,
      sourceY,
      sourceWidth,
      sourceHeight,
      frameX,
      frameY,
      frameWidth,
      frameHeight,
    );
    context.globalCompositeOperation = "screen";
    context.fillStyle = "rgba(128, 91, 55, 0.12)";
    context.fillRect(frameX, frameY, frameWidth, frameHeight);
  }
  context.restore();

  const corner = Math.max(7, Math.min(13, frameWidth * 0.055));
  const brass = "rgba(205, 165, 100, 0.78)";
  context.strokeStyle = brass;
  context.lineWidth = 0.8;
  context.beginPath();
  context.moveTo(frameX, frameY + corner);
  context.lineTo(frameX, frameY);
  context.lineTo(frameX + corner, frameY);
  context.moveTo(frameX + frameWidth - corner, frameY);
  context.lineTo(frameX + frameWidth, frameY);
  context.lineTo(frameX + frameWidth, frameY + corner);
  context.moveTo(frameX + frameWidth, frameY + frameHeight - corner);
  context.lineTo(frameX + frameWidth, frameY + frameHeight);
  context.lineTo(frameX + frameWidth - corner, frameY + frameHeight);
  context.moveTo(frameX + corner, frameY + frameHeight);
  context.lineTo(frameX, frameY + frameHeight);
  context.lineTo(frameX, frameY + frameHeight - corner);
  context.stroke();

  context.beginPath();
  context.moveTo(frameCenter.x - 3.5, frameCenter.y);
  context.lineTo(frameCenter.x + 3.5, frameCenter.y);
  context.moveTo(frameCenter.x, frameCenter.y - 3.5);
  context.lineTo(frameCenter.x, frameCenter.y + 3.5);
  context.strokeStyle = "rgba(219, 183, 119, 0.56)";
  context.lineWidth = 0.65;
  context.stroke();

  context.beginPath();
  context.arc(currentPoint.x, currentPoint.y, 2.15, 0, Math.PI * 2);
  context.fillStyle = COLORS.bone;
  context.shadowColor = "rgba(0, 0, 0, 0.72)";
  context.shadowBlur = 4;
  context.fill();
}

function drawFloorEllipse(context, camera, center, radiusX, radiusZ, fill) {
  const origin = project(center, camera);
  const xPoint = project(point(center.x + radiusX, center.y, center.z), camera);
  const zPoint = project(point(center.x, center.y, center.z + radiusZ), camera);
  const xx = xPoint.x - origin.x;
  const xy = xPoint.y - origin.y;
  const zx = zPoint.x - origin.x;
  const zy = zPoint.y - origin.y;

  context.save();
  context.transform(xx, xy, zx, zy, origin.x, origin.y);
  context.beginPath();
  context.arc(0, 0, 1, 0, Math.PI * 2);
  context.fillStyle = fill;
  context.fill();
  context.restore();
}

function figureJoints(sample) {
  const facing = sample.facing ?? 0;
  const right = { x: Math.cos(facing), z: -Math.sin(facing) };
  const forward = { x: Math.sin(facing), z: Math.cos(facing) };
  const stride = sample.moving ? Math.sin(sample.phase || 0) : 0;
  const armSwing = stride * 0.115;
  const legSwing = stride * 0.16;
  const at = (side, forwardOffset, height) => point(
    sample.x + right.x * side + forward.x * forwardOffset,
    height,
    sample.z + right.z * side + forward.z * forwardOffset,
  );

  return {
    base: at(0, 0, 0.025),
    head: at(0, 0.008, 1.25),
    headSide: at(0.135, 0.008, 1.25),
    headTop: at(0, 0.008, 1.405),
    neck: at(0, 0.005, 1.075),
    shoulderL: at(-0.205, 0, 1.02),
    shoulderR: at(0.205, 0, 1.02),
    waistL: at(-0.125, 0, 0.61),
    waistR: at(0.125, 0, 0.61),
    hipL: at(-0.105, 0, 0.55),
    hipR: at(0.105, 0, 0.55),
    elbowL: at(-0.265, armSwing, 0.76),
    elbowR: at(0.265, -armSwing, 0.76),
    handL: at(-0.245, armSwing * 1.5, 0.52),
    handR: at(0.245, -armSwing * 1.5, 0.52),
    kneeL: at(-0.105, legSwing, 0.29),
    kneeR: at(0.105, -legSwing, 0.29),
    footL: at(-0.105, legSwing * 1.42 + 0.035, 0.055),
    footR: at(0.105, -legSwing * 1.42 + 0.035, 0.055),
  };
}

function limbPath(context, camera, worldPoints, widths) {
  const screen = projected(camera, worldPoints);
  const left = [];
  const right = [];
  for (let index = 0; index < screen.length; index += 1) {
    const before = screen[Math.max(0, index - 1)];
    const after = screen[Math.min(screen.length - 1, index + 1)];
    const dx = after.x - before.x;
    const dy = after.y - before.y;
    const length = Math.hypot(dx, dy) || 1;
    const normalX = -dy / length;
    const normalY = dx / length;
    left.push({ x: screen[index].x + normalX * widths[index], y: screen[index].y + normalY * widths[index] });
    right.push({ x: screen[index].x - normalX * widths[index], y: screen[index].y - normalY * widths[index] });
  }

  context.beginPath();
  context.moveTo(left[0].x, left[0].y);
  context.quadraticCurveTo(left[1].x, left[1].y, left[2].x, left[2].y);
  context.quadraticCurveTo(screen[2].x + (left[2].x - screen[2].x) * 0.7, screen[2].y + (left[2].y - screen[2].y) * 0.7, right[2].x, right[2].y);
  context.quadraticCurveTo(right[1].x, right[1].y, right[0].x, right[0].y);
  context.quadraticCurveTo(screen[0].x + (right[0].x - screen[0].x) * 0.65, screen[0].y + (right[0].y - screen[0].y) * 0.65, left[0].x, left[0].y);
  context.closePath();
}

function torsoPath(context, torso) {
  const [shoulderL, shoulderR, waistR, waistL] = torso;
  context.beginPath();
  context.moveTo(shoulderL.x, shoulderL.y);
  context.quadraticCurveTo(
    (shoulderL.x + shoulderR.x) * 0.5,
    Math.min(shoulderL.y, shoulderR.y) - 2,
    shoulderR.x,
    shoulderR.y,
  );
  context.bezierCurveTo(
    shoulderR.x + 1,
    mix(shoulderR.y, waistR.y, 0.4),
    waistR.x + 1,
    mix(shoulderR.y, waistR.y, 0.82),
    waistR.x,
    waistR.y,
  );
  context.quadraticCurveTo((waistL.x + waistR.x) * 0.5, Math.max(waistL.y, waistR.y) + 1, waistL.x, waistL.y);
  context.bezierCurveTo(
    waistL.x - 1,
    mix(shoulderL.y, waistL.y, 0.82),
    shoulderL.x - 1,
    mix(shoulderL.y, waistL.y, 0.4),
    shoulderL.x,
    shoulderL.y,
  );
  context.closePath();
}

function headSilhouettePath(context, center, radiusX, radiusY) {
  context.beginPath();
  context.moveTo(center.x, center.y - radiusY);
  context.bezierCurveTo(
    center.x + radiusX * 0.72,
    center.y - radiusY,
    center.x + radiusX,
    center.y - radiusY * 0.42,
    center.x + radiusX * 0.9,
    center.y + radiusY * 0.12,
  );
  context.bezierCurveTo(
    center.x + radiusX * 0.78,
    center.y + radiusY * 0.7,
    center.x + radiusX * 0.38,
    center.y + radiusY,
    center.x,
    center.y + radiusY * 0.91,
  );
  context.bezierCurveTo(
    center.x - radiusX * 0.38,
    center.y + radiusY,
    center.x - radiusX * 0.78,
    center.y + radiusY * 0.7,
    center.x - radiusX * 0.9,
    center.y + radiusY * 0.12,
  );
  context.bezierCurveTo(
    center.x - radiusX,
    center.y - radiusY * 0.42,
    center.x - radiusX * 0.72,
    center.y - radiusY,
    center.x,
    center.y - radiusY,
  );
  context.closePath();
}

function drawFigure(context, camera, sample, ghost = false) {
  const joints = figureJoints(sample);
  const baseProjection = project(joints.base, camera);
  const unit = clamp(baseProjection.scale * 0.042, 2.2, 6.2);

  if (!ghost) {
    context.save();
    context.filter = "blur(5px)";
    drawFloorEllipse(
      context,
      camera,
      point(sample.x + 0.28, 0.019, sample.z - 0.52),
      0.3,
      1.12,
      "rgba(0, 0, 0, 0.46)",
    );
    context.restore();
    drawFloorEllipse(
      context,
      camera,
      point(sample.x, 0.02, sample.z - 0.02),
      0.23,
      0.16,
      "rgba(122, 83, 47, 0.16)",
    );
  }

  const torso = projected(camera, [joints.shoulderL, joints.shoulderR, joints.waistR, joints.waistL]);
  const head = project(joints.head, camera);
  const headSide = project(joints.headSide, camera);
  const headTop = project(joints.headTop, camera);
  const neck = project(joints.neck, camera);
  const radiusX = Math.max(3.2, Math.hypot(headSide.x - head.x, headSide.y - head.y));
  const radiusY = Math.max(4, Math.hypot(headTop.x - head.x, headTop.y - head.y));
  const silhouette = context.createLinearGradient(head.x, head.y - radiusY, baseProjection.x, baseProjection.y);
  silhouette.addColorStop(0, "#fff8e7");
  silhouette.addColorStop(0.48, COLORS.bone);
  silhouette.addColorStop(1, COLORS.boneShade);

  context.save();
  context.lineJoin = "round";
  context.lineCap = "round";
  const fillOrStroke = () => {
    if (ghost) {
      context.strokeStyle = "rgba(205, 162, 94, 0.62)";
      context.lineWidth = Math.max(1, unit * 0.28);
      context.stroke();
    } else {
      context.fillStyle = silhouette;
      context.fill();
    }
  };

  limbPath(context, camera, [joints.shoulderL, joints.elbowL, joints.handL], [unit * 0.72, unit * 0.56, unit * 0.38]);
  fillOrStroke();
  limbPath(context, camera, [joints.shoulderR, joints.elbowR, joints.handR], [unit * 0.72, unit * 0.56, unit * 0.38]);
  fillOrStroke();
  limbPath(context, camera, [joints.hipL, joints.kneeL, joints.footL], [unit * 0.94, unit * 0.72, unit * 0.52]);
  fillOrStroke();
  limbPath(context, camera, [joints.hipR, joints.kneeR, joints.footR], [unit * 0.94, unit * 0.72, unit * 0.52]);
  fillOrStroke();

  if (!ghost) {
    const hipL = project(joints.hipL, camera);
    const hipR = project(joints.hipR, camera);
    const pelvisX = (hipL.x + hipR.x) * 0.5;
    const pelvisY = (hipL.y + hipR.y) * 0.5;
    context.beginPath();
    context.ellipse(
      pelvisX,
      pelvisY - unit * 0.12,
      Math.hypot(hipR.x - hipL.x, hipR.y - hipL.y) * 0.54 + unit * 0.35,
      unit * 0.82,
      0,
      0,
      Math.PI * 2,
    );
    context.fillStyle = silhouette;
    context.fill();
  }

  torsoPath(context, torso);
  fillOrStroke();

  if (!ghost) {
    for (const joint of [joints.shoulderL, joints.shoulderR, joints.hipL, joints.hipR]) {
      const screen = project(joint, camera);
      context.beginPath();
      context.arc(screen.x, screen.y, unit * 0.67, 0, Math.PI * 2);
      context.fillStyle = silhouette;
      context.fill();
    }
  }

  const shoulderCenter = {
    x: (torso[0].x + torso[1].x) * 0.5,
    y: (torso[0].y + torso[1].y) * 0.5,
  };
  context.beginPath();
  context.moveTo(shoulderCenter.x, shoulderCenter.y - unit * 0.1);
  context.lineTo(neck.x, neck.y);
  context.lineTo(head.x, head.y + radiusY * 0.62);
  context.lineWidth = radiusX * 0.92;
  if (ghost) {
    context.strokeStyle = "rgba(205, 162, 94, 0.62)";
    context.stroke();
  } else {
    context.strokeStyle = COLORS.bone;
    context.stroke();
  }

  headSilhouettePath(context, head, radiusX, radiusY);
  if (ghost) {
    context.strokeStyle = "rgba(205, 162, 94, 0.62)";
    context.lineWidth = Math.max(1, unit * 0.28);
    context.stroke();
  } else {
    context.fillStyle = silhouette;
    context.fill();
    context.strokeStyle = "rgba(104, 78, 50, 0.34)";
    context.lineWidth = Math.max(0.55, unit * 0.1);
    context.stroke();

    context.beginPath();
    context.ellipse(
      head.x - radiusX * 0.2,
      head.y - radiusY * 0.28,
      radiusX * 0.22,
      radiusY * 0.18,
      -0.35,
      0,
      Math.PI * 2,
    );
    context.fillStyle = "rgba(255, 253, 240, 0.3)";
    context.fill();
  }
  context.restore();
}

function sampleHistory(history, timestamp, fallback) {
  if (history.length === 0) return { ...fallback, moving: false };
  if (timestamp <= history[0].time) return { ...history[0] };
  const last = history[history.length - 1];
  if (timestamp >= last.time) {
    return { ...last, moving: timestamp - last.time < 115 && last.moving };
  }

  for (let index = history.length - 1; index > 0; index -= 1) {
    const later = history[index];
    const earlier = history[index - 1];
    if (timestamp < earlier.time) continue;
    const amount = clamp((timestamp - earlier.time) / Math.max(1, later.time - earlier.time));
    return {
      x: mix(earlier.x, later.x, amount),
      z: mix(earlier.z, later.z, amount),
      facing: amount < 0.5 ? earlier.facing : later.facing,
      phase: mix(earlier.phase, later.phase, amount),
      moving: earlier.moving || later.moving,
      time: timestamp,
    };
  }
  return { ...fallback, moving: false };
}

function loadImage(url) {
  if (typeof Image === "undefined") return Promise.resolve(null);
  return new Promise((resolve) => {
    const image = new Image();
    image.decoding = "async";
    image.onload = () => resolve(image);
    image.onerror = () => resolve(null);
    image.src = url;
  });
}

export function createScene(canvas, { reducedMotion = false, variant = "hero" } = {}) {
  if (!canvas || typeof canvas.getContext !== "function") {
    throw new TypeError("createScene requires a canvas element");
  }

  const context = canvas.getContext("2d", { alpha: true, desynchronized: true });
  if (!context) throw new Error("Canvas 2D is unavailable");

  const initialPosition = variant === "hero"
    ? { x: mix(-2.55, 2.55, 0.76), z: mix(1.78, -1.78, 0.26) }
    : { x: 0, z: -0.4272 };

  const state = {
    width: 1,
    height: 1,
    dpr: 1,
    view: 0,
    previousView: 0,
    cutStartedAt: -Infinity,
    cutDuration: 320,
    pointerActive: false,
    current: { ...initialPosition, facing: 0.2, phase: 0, moving: false },
    history: [],
    progress: 0,
    reducedMotion: Boolean(reducedMotion),
    paused: false,
    disposed: false,
    dirty: true,
    frame: 0,
    revealStartedAt: now(),
    revealDuration: 980,
    replay: null,
    roomImage: null,
    lastInputAt: -Infinity,
    lastFrameAt: -Infinity,
  };

  const metrics = {
    frames: 0,
    resizeCount: 0,
    dpr: 1,
    width: 1,
    height: 1,
    view: 0,
    historySamples: 0,
    inputs: 0,
    replaying: false,
    paused: false,
  };

  state.history.push({ ...state.current, time: now(), moving: false });

  function resize(rect = canvas.getBoundingClientRect()) {
    if (state.disposed) return;
    const cssWidth = Math.max(1, Math.round(rect.width || canvas.clientWidth || 300));
    const cssHeight = Math.max(1, Math.round(rect.height || canvas.clientHeight || 150));
    const dpr = Math.min(2, Math.max(1, window.devicePixelRatio || 1));
    const pixelWidth = Math.round(cssWidth * dpr);
    const pixelHeight = Math.round(cssHeight * dpr);
    if (canvas.width !== pixelWidth || canvas.height !== pixelHeight) {
      canvas.width = pixelWidth;
      canvas.height = pixelHeight;
    }
    state.width = cssWidth;
    state.height = cssHeight;
    state.dpr = dpr;
    metrics.resizeCount += 1;
    metrics.dpr = dpr;
    metrics.width = cssWidth;
    metrics.height = cssHeight;
    state.dirty = true;
    requestFrame();
  }

  function getReplaySample(timestamp) {
    if (!state.replay) return null;
    const elapsed = timestamp - state.replay.startedAt;
    if (elapsed >= state.replay.duration) {
      state.replay = null;
      metrics.replaying = false;
      return null;
    }
    const sourceTime = mix(state.replay.from, state.replay.to, clamp(elapsed / state.replay.duration));
    return sampleHistory(state.replay.samples, sourceTime, state.current);
  }

  function cutState(timestamp) {
    if (state.reducedMotion || !Number.isFinite(state.cutStartedAt)) {
      return { view: state.view, alpha: 1, active: false };
    }
    const elapsed = timestamp - state.cutStartedAt;
    if (elapsed >= state.cutDuration) {
      state.cutStartedAt = -Infinity;
      return { view: state.view, alpha: 1, active: false };
    }
    const amount = clamp(elapsed / state.cutDuration);
    if (amount < 0.44) {
      return { view: state.previousView, alpha: mix(1, 0.13, amount / 0.44), active: true };
    }
    return { view: state.view, alpha: mix(0.13, 1, (amount - 0.44) / 0.56), active: true };
  }

  function render(timestamp) {
    const width = state.width;
    const height = state.height;
    context.setTransform(state.dpr, 0, 0, state.dpr, 0, 0);
    context.clearRect(0, 0, width, height);

    const cut = cutState(timestamp);
    const reveal = state.reducedMotion
      ? 1
      : clamp((timestamp - state.revealStartedAt) / state.revealDuration);
    const revealEase = 1 - Math.pow(1 - reveal, 3);

    const current = {
      ...state.current,
      moving: timestamp - state.lastInputAt < 110 && state.current.moving,
    };
    const replaySample = getReplaySample(timestamp);
    const observed = replaySample || sampleHistory(state.history, timestamp - 450, current);

    context.save();
    context.globalAlpha = cut.alpha * (0.12 + revealEase * 0.88);
    context.beginPath();
    const revealMargin = height * (1 - revealEase) * 0.18;
    context.rect(0, revealMargin, width, Math.max(1, height - revealMargin * 2));
    context.clip();

    if (variant === "hero") {
      drawHeroScene(context, state.roomImage, width, height, current, observed, state.reducedMotion);
    } else {
      const camera = cameraFrame(VIEWS[cut.view], width, height, variant);
      drawArchitecture(context, camera, state.roomImage, state.progress);
      drawFigure(context, camera, observed, true);
      drawFigure(context, camera, current, false);
    }
    context.restore();

    metrics.frames += 1;
    metrics.view = state.view;
    metrics.historySamples = state.history.length;
    metrics.replaying = Boolean(state.replay);
    metrics.paused = state.paused;
    canvas.dataset.frames = String(metrics.frames);
    canvas.dataset.view = String(state.view);
    canvas.dataset.historySamples = String(state.history.length);
    canvas.dataset.inputs = String(metrics.inputs);
    canvas.dataset.replaying = String(Boolean(state.replay));
    canvas.dataset.paused = String(state.paused);
    canvas.dataset.currentX = current.x.toFixed(4);
    canvas.dataset.currentZ = current.z.toFixed(4);
    canvas.dataset.delayedX = observed.x.toFixed(4);
    canvas.dataset.delayedZ = observed.z.toFixed(4);
  }

  function isAnimating(timestamp) {
    const revealing = !state.reducedMotion && timestamp - state.revealStartedAt < state.revealDuration;
    const cutting = !state.reducedMotion && timestamp - state.cutStartedAt < state.cutDuration;
    const delaySettling = timestamp - state.lastInputAt < 620;
    return revealing || cutting || delaySettling || Boolean(state.replay);
  }

  function loop(timestamp) {
    state.frame = 0;
    if (state.disposed || state.paused) return;
    if (state.dirty || isAnimating(timestamp)) {
      render(timestamp);
      state.dirty = false;
      state.lastFrameAt = timestamp;
    }
    if (isAnimating(timestamp)) requestFrame();
  }

  function requestFrame() {
    if (state.disposed || state.paused || state.frame) return;
    state.frame = requestAnimationFrame(loop);
  }

  function setView(index) {
    const next = clamp(Math.round(Number(index) || 0), 0, VIEWS.length - 1);
    if (next === state.view) return;
    state.previousView = state.view;
    state.view = next;
    metrics.view = next;
    state.cutStartedAt = state.reducedMotion ? -Infinity : now();
    state.dirty = true;
    requestFrame();
  }

  function setPointer(x, y, active = true) {
    if (!Number.isFinite(Number(x)) || !Number.isFinite(Number(y))) return;
    const nextX = mix(-2.55, 2.55, clamp(Number(x)));
    const nextZ = mix(1.78, -1.78, clamp(Number(y)));
    const deltaX = nextX - state.current.x;
    const deltaZ = nextZ - state.current.z;
    const distance = Math.hypot(deltaX, deltaZ);
    const timestamp = now();
    state.pointerActive = Boolean(active);

    if (distance > 0.0005) {
      const previous = { ...state.current, moving: false };
      const last = state.history[state.history.length - 1];
      if (last && timestamp - last.time > 80) {
        state.history.push({ ...previous, time: timestamp - 1 });
      }
      state.current.facing = Math.atan2(deltaX, deltaZ);
      state.current.phase += distance * 8.2;
      state.current.x = nextX;
      state.current.z = nextZ;
      state.current.moving = true;
      state.lastInputAt = timestamp;
      metrics.inputs += 1;
      const latest = state.history[state.history.length - 1];
      if (!latest || distance > 0.012 || timestamp - latest.time > 42) {
        state.history.push({ ...state.current, time: timestamp });
      }
      const cutoff = timestamp - 8000;
      while (state.history.length > 2 && state.history[1].time < cutoff) {
        state.history.shift();
      }
    } else if (!active) {
      state.current.moving = false;
    }

    state.dirty = true;
    requestFrame();
  }

  function setProgress(value) {
    const next = clamp(Number(value) || 0);
    if (Math.abs(next - state.progress) < 0.002) return;
    state.progress = next;
    state.dirty = true;
    requestFrame();
  }

  function setReducedMotion(value) {
    state.reducedMotion = Boolean(value);
    if (state.reducedMotion) {
      state.cutStartedAt = -Infinity;
      state.revealStartedAt = -Infinity;
      state.replay = null;
      metrics.replaying = false;
    } else {
      state.revealStartedAt = now() - state.revealDuration;
    }
    state.dirty = true;
    requestFrame();
  }

  function setPaused(value) {
    state.paused = Boolean(value);
    metrics.paused = state.paused;
    canvas.dataset.paused = String(state.paused);
    if (state.paused && state.frame) {
      cancelAnimationFrame(state.frame);
      state.frame = 0;
    } else if (!state.paused) {
      state.dirty = true;
      requestFrame();
    }
  }

  function replay() {
    if (state.reducedMotion || state.history.length < 2) {
      state.dirty = true;
      requestFrame();
      return;
    }
    const timestamp = now();
    const samples = state.history.map((sample) => ({ ...sample }));
    state.replay = {
      samples,
      from: samples[0].time,
      to: samples[samples.length - 1].time,
      startedAt: timestamp,
      duration: 6000,
    };
    metrics.replaying = true;
    state.dirty = true;
    requestFrame();
  }

  function dispose() {
    if (state.disposed) return;
    state.disposed = true;
    if (state.frame) cancelAnimationFrame(state.frame);
    resizeObserver?.disconnect();
    window.removeEventListener("resize", fallbackResize);
    state.history.length = 0;
    state.replay = null;
    context.setTransform(1, 0, 0, 1, 0, 0);
    context.clearRect(0, 0, canvas.width, canvas.height);
  }

  const fallbackResize = () => resize();
  const resizeObserver = typeof ResizeObserver === "undefined"
    ? null
    : new ResizeObserver((entries) => {
        const entry = entries[0];
        if (entry) resize(entry.contentRect);
      });

  if (resizeObserver) resizeObserver.observe(canvas);
  else window.addEventListener("resize", fallbackResize, { passive: true });
  resize();

  const ready = loadImage(new URL("./assets/room.jpg", import.meta.url).href)
    .then((image) => {
      state.roomImage = image;
      state.dirty = true;
      requestFrame();
    })
    .catch(() => undefined);

  return {
    ready,
    setView,
    setPointer,
    setProgress,
    setReducedMotion,
    setPaused,
    replay,
    dispose,
    metrics,
    get debug() {
      return {
        view: state.view,
        pointer: { x: state.current.x, z: state.current.z, active: state.pointerActive },
        observed: sampleHistory(state.history, now() - 450, state.current),
        historySamples: state.history.length,
        replaying: Boolean(state.replay),
        reducedMotion: state.reducedMotion,
        paused: state.paused,
      };
    },
  };
}
