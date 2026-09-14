import { createReadStream } from "node:fs";
import { realpath, stat } from "node:fs/promises";
import { dirname, extname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { createServer } from "node:http";
import { fileURLToPath } from "node:url";

const HOST = "127.0.0.1";
const DEFAULT_PORT = 4173;
const requestedRoot = process.argv[2] ?? ".";
const moduleRoot = dirname(fileURLToPath(import.meta.url));
const root = resolve(moduleRoot, requestedRoot);
const rootReal = await realpath(root);
const port = parsePort(process.env.PORT);

const MIME_TYPES = new Map([
  [".html", "text/html; charset=utf-8"],
  [".css", "text/css; charset=utf-8"],
  [".js", "text/javascript; charset=utf-8"],
  [".mjs", "text/javascript; charset=utf-8"],
  [".json", "application/json; charset=utf-8"],
  [".webmanifest", "application/manifest+json; charset=utf-8"],
  [".xml", "application/xml; charset=utf-8"],
  [".png", "image/png"],
  [".webp", "image/webp"],
  [".woff2", "font/woff2"],
  [".txt", "text/plain; charset=utf-8"]
]);

function parsePort(value) {
  if (value === undefined) return DEFAULT_PORT;
  if (!/^\d+$/.test(value)) throw new Error("PORT must be an integer between 1 and 65535.");
  const parsed = Number(value);
  if (parsed < 1 || parsed > 65535) throw new Error("PORT must be an integer between 1 and 65535.");
  return parsed;
}

function isWithinRoot(candidate) {
  const fromRoot = relative(rootReal, candidate);
  return fromRoot === "" || (!fromRoot.startsWith(`..${sep}`) && fromRoot !== ".." && !isAbsolute(fromRoot));
}

function sendText(response, statusCode, message) {
  response.writeHead(statusCode, { "Content-Type": "text/plain; charset=utf-8" });
  response.end(message);
}

const server = createServer(async (request, response) => {
  if (request.method !== "GET" && request.method !== "HEAD") {
    response.setHeader("Allow", "GET, HEAD");
    sendText(response, 405, "Method not allowed");
    return;
  }

  try {
    const rawTarget = request.url ?? "/";
    const rawPath = rawTarget.split("?", 1)[0].split("#", 1)[0];
    const decodedRawPath = decodeURIComponent(rawPath).replaceAll("\\", "/");
    if (decodedRawPath.split("/").includes("..")) {
      sendText(response, 403, "Forbidden");
      return;
    }

    const requestUrl = new URL(rawTarget, "http://localhost");
    const pathname = decodeURIComponent(requestUrl.pathname).replaceAll("\\", "/");
    const segments = pathname.split("/");
    if (pathname.includes("\0") || segments.includes("..")) {
      sendText(response, 403, "Forbidden");
      return;
    }

    const relativePath = pathname === "/" ? "index.html" : pathname.replace(/^\/+/, "");
    let candidate = resolve(rootReal, relativePath);
    if (!isWithinRoot(candidate)) {
      sendText(response, 403, "Forbidden");
      return;
    }

    let fileStat = await stat(candidate);
    if (fileStat.isDirectory()) {
      candidate = join(candidate, "index.html");
      fileStat = await stat(candidate);
    }
    if (!fileStat.isFile()) throw Object.assign(new Error("Not found"), { code: "ENOENT" });

    const fileReal = await realpath(candidate);
    if (!isWithinRoot(fileReal)) {
      sendText(response, 403, "Forbidden");
      return;
    }

    const contentType = MIME_TYPES.get(extname(fileReal).toLowerCase()) ?? "application/octet-stream";
    const cacheControl = /\.(?:webp|woff2)$/i.test(fileReal) ? "public, max-age=3600" : "no-cache";
    response.writeHead(200, {
      "Content-Type": contentType,
      "Content-Length": fileStat.size,
      "Cache-Control": cacheControl,
      "X-Content-Type-Options": "nosniff",
      "Referrer-Policy": "no-referrer"
    });

    if (request.method === "HEAD") response.end();
    else createReadStream(fileReal).pipe(response);
  } catch (error) {
    if (error instanceof URIError) sendText(response, 400, "Bad request");
    else if (error?.code === "ENOENT" || error?.code === "ENOTDIR") sendText(response, 404, "Not found");
    else {
      console.error(error);
      sendText(response, 500, "Internal server error");
    }
  }
});

server.listen(port, HOST, () => {
  console.log(`廻リ視 Web: http://${HOST}:${port}/`);
  console.log(`Serving: ${rootReal}`);
});
