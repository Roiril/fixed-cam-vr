(function () {
  'use strict';

  // Port of Assets/Scripts/Streaming/IntroFractureMesh.cs (front outlines only).
  const seed = 20260915;
  const impact = [-0.143, 0.108];
  const rayCount = 15;
  const radii = [0.055, 0.115, 0.19, 0.29, 0.42, 0.58, 0.78, 1.02, 1.32, 1.70];
  const minArea = 1e-12;
  const halfAngle = Math.atan(2);

  // Unity's System.Random uses the subtractive .NET Framework generator.
  function makeRandom(value) {
    const values = new Array(56).fill(0);
    let mj = 161803398 - Math.abs(value);
    values[55] = mj;
    let mk = 1;
    for (let i = 1; i < 55; i++) {
      const ii = (21 * i) % 55;
      values[ii] = mk;
      mk = mj - mk;
      if (mk < 0) mk += 2147483647;
      mj = values[ii];
    }
    for (let j = 0; j < 4; j++) {
      for (let i = 1; i < 56; i++) {
        values[i] -= values[1 + (i + 30) % 55];
        if (values[i] < 0) values[i] += 2147483647;
      }
    }
    let inext = 0;
    let inextp = 21;
    function sample() {
      if (++inext >= 56) inext = 1;
      if (++inextp >= 56) inextp = 1;
      let result = values[inext] - values[inextp];
      if (result === 2147483647) result--;
      if (result < 0) result += 2147483647;
      values[inext] = result;
      return result / 2147483647;
    }
    return { nextDouble: sample, next: max => Math.floor(sample() * max) };
  }

  const subtract = (a, b) => [a[0] - b[0], a[1] - b[1]];
  const cross = (a, b) => a[0] * b[1] - a[1] * b[0];
  const dot = (a, b) => a[0] * b[0] + a[1] * b[1];
  const distanceSquared = (a, b) => dot(subtract(a, b), subtract(a, b));
  const edge = (a, b) => a < b ? [a, b] : [b, a];
  const edgeKey = ([a, b]) => `${a},${b}`;
  const toLocal = p => [
    Math.fround(Math.tan(p[0] * halfAngle) * 0.5 * Math.fround(0.30)),
    Math.fround(Math.tan(p[1] * halfAngle) * 0.5 * Math.fround(0.30)),
  ];

  function addPoint(points, point) {
    if (!points.some(p => distanceSquared(p, point) < 1e-16)) points.push(point);
  }

  function buildGridSites(random) {
    const sites = [];
    const width = 2 / 6;
    const height = 2 / 4;
    for (let y = 0; y < 4; y++) {
      for (let x = 0; x < 6; x++) {
        sites.push([
          -1 + (x + 0.5) * width + (random.nextDouble() * 2 - 1) * width * 0.34,
          -1 + (y + 0.5) * height + (random.nextDouble() * 2 - 1) * height * 0.34,
        ]);
      }
    }
    return sites;
  }

  function buildFracturePoints(sites, random) {
    const points = [[-1, -1], [1, -1], [1, 1], [-1, 1]];
    const horizontal = [-0.91, -0.78, -0.59, -0.34, -0.08, 0.13, 0.47, 0.72, 0.93];
    const vertical = [-0.88, -0.63, -0.29, 0.04, 0.38, 0.79];
    for (const x of horizontal) {
      addPoint(points, [x, -1]);
      addPoint(points, [x, 1]);
    }
    for (const y of vertical) {
      addPoint(points, [-1, y]);
      addPoint(points, [1, y]);
    }
    addPoint(points, impact);
    const sector = Math.PI * 2 / rayCount;
    const angles = [];
    const drift = [];
    for (let ray = 0; ray < rayCount; ray++) {
      angles.push(sector * ray + (random.nextDouble() * 2 - 1) * sector * 0.30);
      drift.push((random.nextDouble() * 2 - 1) * 0.045);
    }
    for (let ring = 0; ring < radii.length; ring++) {
      for (let ray = 0; ray < rayCount; ray++) {
        const skip = random.nextDouble();
        const angle = angles[ray] + drift[ray] * ring
          + (random.nextDouble() * 2 - 1) * sector * 0.36;
        const radius = radii[ring] * (1 + (random.nextDouble() * 2 - 1) * 0.22);
        if (ring >= 2 && skip < 0.12) continue;
        const point = [impact[0] + Math.cos(angle) * radius,
          impact[1] + Math.sin(angle) * radius];
        if (Math.abs(point[0]) <= 0.985 && Math.abs(point[1]) <= 0.985) addPoint(points, point);
      }
    }
    for (let ray = 0; ray < rayCount; ray += 3) {
      const angle = angles[ray] + drift[ray] * 1.5;
      const along = [Math.cos(angle), Math.sin(angle)];
      const across = [-along[1], along[0]];
      for (let i = 0; i < 6; i++) {
        const longitudinal = 0.08 + random.nextDouble() * 0.36;
        const lateral = (random.nextDouble() * 2 - 1) * 0.018;
        const point = [impact[0] + along[0] * longitudinal + across[0] * lateral,
          impact[1] + along[1] * longitudinal + across[1] * lateral];
        if (Math.abs(point[0]) <= 0.985 && Math.abs(point[1]) <= 0.985) addPoint(points, point);
      }
    }
    for (const site of sites) addPoint(points, site);
    return points;
  }

  function circumcircleContains(a, b, c, point) {
    const [ax, ay] = subtract(a, point);
    const [bx, by] = subtract(b, point);
    const [cx, cy] = subtract(c, point);
    const determinant = (ax * ax + ay * ay) * (bx * cy - by * cx)
      - (bx * bx + by * by) * (ax * cy - ay * cx)
      + (cx * cx + cy * cy) * (ax * by - ay * bx);
    return determinant > 1e-13;
  }

  function triangulate(points) {
    const count = points.length;
    const working = points.concat([[-16, -8], [16, -8], [0, 16]]);
    const triangles = [[count, count + 1, count + 2]];
    for (let pointIndex = 0; pointIndex < count; pointIndex++) {
      const boundary = [];
      for (let triangleIndex = triangles.length - 1; triangleIndex >= 0; triangleIndex--) {
        const triangle = triangles[triangleIndex];
        if (!circumcircleContains(...triangle.map(index => working[index]), working[pointIndex])) continue;
        for (const pair of [[triangle[0], triangle[1]], [triangle[1], triangle[2]],
          [triangle[2], triangle[0]]]) {
          const normalized = edge(...pair);
          const existing = boundary.findIndex(item => item[0] === normalized[0] && item[1] === normalized[1]);
          if (existing >= 0) boundary.splice(existing, 1);
          else boundary.push(normalized);
        }
        triangles.splice(triangleIndex, 1);
      }
      for (const [a, b] of boundary) {
        const orientation = cross(subtract(working[b], working[a]),
          subtract(working[pointIndex], working[a]));
        if (Math.abs(orientation) <= minArea) continue;
        triangles.push(orientation > 0 ? [a, b, pointIndex] : [b, a, pointIndex]);
      }
    }
    return triangles.filter(triangle => triangle.every(index => index < count));
  }

  function twiceArea(polygon) {
    let sum = 0;
    for (let i = 0; i < polygon.length; i++) sum += cross(polygon[i], polygon[(i + 1) % polygon.length]);
    return sum;
  }

  function convex(polygon) {
    return polygon.every((p, i) => cross(subtract(polygon[(i + 1) % polygon.length], p),
      subtract(polygon[(i + 2) % polygon.length], polygon[(i + 1) % polygon.length])) > minArea);
  }

  function rectangleLike(polygon) {
    return polygon.every((p, i) => {
      const incoming = subtract(polygon[(i + polygon.length - 1) % polygon.length], p);
      const outgoing = subtract(polygon[(i + 1) % polygon.length], p);
      return Math.abs(dot(incoming, outgoing)) / Math.sqrt(dot(incoming, incoming) * dot(outgoing, outgoing)) <= 0.21;
    });
  }

  function polygonCentroid(polygon) {
    let x = 0;
    let y = 0;
    let area = 0;
    for (let i = 0; i < polygon.length; i++) {
      const a = polygon[i];
      const b = polygon[(i + 1) % polygon.length];
      const factor = cross(a, b);
      area += factor;
      x += (a[0] + b[0]) * factor;
      y += (a[1] + b[1]) * factor;
    }
    return [x / (3 * area), y / (3 * area)];
  }

  function nearestMacro(point, sites) {
    let nearest = 0;
    let nearestDistance = Infinity;
    for (let i = 0; i < sites.length; i++) {
      const distance = distanceSquared(point, sites[i]);
      if (distance < nearestDistance) {
        nearest = i;
        nearestDistance = distance;
      }
    }
    return nearest;
  }

  function buildPieces(points, triangles, sites, random) {
    const owners = new Map();
    const candidates = [];
    triangles.forEach((triangle, index) => {
      for (const pair of [[triangle[0], triangle[1]], [triangle[1], triangle[2]],
        [triangle[2], triangle[0]]]) {
        const normalized = edge(...pair);
        const key = edgeKey(normalized);
        if (owners.has(key)) candidates.push([owners.get(key), index, normalized]);
        else owners.set(key, index);
      }
    });
    for (let i = candidates.length - 1; i > 0; i--) {
      const swap = random.next(i + 1);
      [candidates[i], candidates[swap]] = [candidates[swap], candidates[i]];
    }
    const target = Math.floor(triangles.length / 4);
    let quadCount = 0;
    const consumed = new Array(triangles.length).fill(false);
    const mergedAt = new Array(triangles.length);
    for (const [firstIndex, secondIndex, shared] of candidates) {
      if (quadCount >= target) break;
      if (consumed[firstIndex] || consumed[secondIndex]) continue;
      const first = triangles[firstIndex];
      const second = triangles[secondIndex];
      const centroid = triangle => [
        (points[triangle[0]][0] + points[triangle[1]][0] + points[triangle[2]][0]) / 3,
        (points[triangle[0]][1] + points[triangle[1]][1] + points[triangle[2]][1]) / 3,
      ];
      if (nearestMacro(centroid(first), sites) !== nearestMacro(centroid(second), sites)) continue;
      const opposite = triangle => triangle.find(vertex => vertex !== shared[0] && vertex !== shared[1]);
      const vertices = [shared[0], opposite(first), shared[1], opposite(second)];
      let angular = vertices.map(vertex => points[vertex]);
      if (twiceArea(angular) < 0) {
        [vertices[1], vertices[3]] = [vertices[3], vertices[1]];
        angular = vertices.map(vertex => points[vertex]);
      }
      if (!convex(angular) || rectangleLike(angular)) continue;
      const local = vertices.map(vertex => toLocal(points[vertex]));
      if (!convex(local) || rectangleLike(local)) continue;
      consumed[firstIndex] = consumed[secondIndex] = true;
      mergedAt[Math.min(firstIndex, secondIndex)] = vertices;
      quadCount++;
    }
    if (quadCount < target) throw new Error(`Only ${quadCount} valid fracture quads were available; expected ${target}.`);
    const pieces = [];
    for (let i = 0; i < triangles.length; i++) {
      if (mergedAt[i]) pieces.push(mergedAt[i]);
      else if (!consumed[i]) pieces.push(triangles[i]);
    }
    return pieces;
  }

  function create() {
    const random = makeRandom(seed);
    const sites = buildGridSites(random);
    const points = buildFracturePoints(sites, random);
    const triangles = triangulate(points);
    const pieces = buildPieces(points, triangles, sites, random);
    const localSites = sites.map(toLocal);
    const localImpact = toLocal(impact);
    const distances = localSites.map(site => Math.hypot(...subtract(site, localImpact)));
    const minDistance = Math.min(...distances);
    const maxDistance = Math.max(...distances);
    const output = pieces.map(vertices => {
      const polygon = vertices.map(vertex => toLocal(points[vertex]));
      const area = twiceArea(polygon) / 2;
      if (area <= minArea) throw new Error('Fracture shard became degenerate after projection.');
      const center = polygonCentroid(polygon).map(Math.fround);
      const index = nearestMacro(polygonCentroid(vertices.map(vertex => points[vertex])), sites);
      return {
        polygon,
        center,
        size: Math.fround(Math.sqrt(Math.fround(area))),
        macro: [localSites[index][0], localSites[index][1],
          Math.fround((distances[index] - minDistance) / (maxDistance - minDistance) * 0.14), index],
        closer: 0,
      };
    });
    const areas = pieces.map(vertices => Math.fround(twiceArea(vertices.map(vertex => toLocal(points[vertex]))) / 2));
    const candidates = output.map((piece, index) => ({ piece, index }))
      .filter(({ piece }) => piece.polygon.some(point =>
        Math.abs(point[0]) >= 0.30 - 1e-5 || Math.abs(point[1]) >= 0.30 - 1e-5))
      .sort((a, b) => areas[b.index] - areas[a.index] || a.index - b.index);
    if (candidates.length < 3) throw new Error('Fracture mesh has too few outer shards for edge closers.');
    for (const candidate of candidates.slice(0, 3)) candidate.piece.closer = 1;
    return output;
  }

  window.MAWARIMI_FRACTURE_GEOMETRY = { create };
}());
