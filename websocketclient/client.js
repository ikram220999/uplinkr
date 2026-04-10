/**
 * WebSocket bridge client:
 * - Connects to ws://127.0.0.1:4001/ (the C# bridge)
 * - Receives { type:"request", id, request:{ method, path, headers, body, isBase64? } }
 * - Forwards to http://localhost:8080{path}
 * - Replies { type:"response", id, response:{ status, headers, body, isBase64? } }
 *
 * Requirements:
 *   npm i ws
 * Run:
 *   node client.js
 */

/* eslint-disable no-console */

const WebSocket = require("ws");

const BRIDGE_WS_URL = process.env.BRIDGE_WS_URL ?? "ws://127.0.0.1:4001/";
const TARGET_HTTP_BASE = process.env.TARGET_HTTP_BASE ?? "http://localhost:8080";
const RECONNECT_MS = Number(process.env.RECONNECT_MS ?? 1000);

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

function isProbablyUtf8(bytes) {
  // Heuristic: decode+re-encode roundtrip equality.
  const text = Buffer.from(bytes).toString("utf8");
  const roundtrip = Buffer.from(text, "utf8");
  return roundtrip.equals(Buffer.from(bytes));
}

function sanitizeRequestHeaders(headers) {
  const out = {};
  for (const [k, v] of Object.entries(headers ?? {})) {
    if (v == null) continue;
    const key = String(k);
    const lower = key.toLowerCase();

    // Hop-by-hop headers / things fetch will manage.
    if (
      lower === "host" ||
      lower === "connection" ||
      lower === "content-length" ||
      lower === "transfer-encoding" ||
      lower === "upgrade" ||
      lower === "sec-websocket-key" ||
      lower === "sec-websocket-version" ||
      lower === "sec-websocket-extensions" ||
      lower === "sec-websocket-protocol"
    ) {
      continue;
    }

    // Avoid upstream compression surprises unless you want it.
    if (lower === "accept-encoding") continue;

    out[key] = String(v);
  }
  return out;
}

function responseHeadersToObject(headers) {
  const obj = {};
  for (const [k, v] of headers.entries()) {
    // If multiple headers with same key exist, fetch combines them with comma.
    obj[k] = v;
  }
  return obj;
}

async function handleBridgeRequest(ws, msg) {
  const id = msg?.id;
  const req = msg?.request;

  if (!id || !req || typeof req !== "object") return;

  const method = (req.method ?? "GET").toUpperCase();
  const path = req.path ?? "/";
  const url = new URL(path, TARGET_HTTP_BASE).toString();

  let bodyBytes = undefined;
  if (req.body != null && req.body !== "") {
    if (req.isBase64) bodyBytes = Buffer.from(String(req.body), "base64");
    else bodyBytes = Buffer.from(String(req.body), "utf8");
  }

  const headers = sanitizeRequestHeaders(req.headers);

  let status = 502;
  let respHeaders = { "content-type": "text/plain; charset=utf-8" };
  let respBodyBytes = Buffer.from("Bad Gateway", "utf8");
  let isBase64 = false;

  try {
    const res = await fetch(url, {
      method,
      headers,
      body: bodyBytes,
      redirect: "manual",
    });

    status = res.status;
    respHeaders = responseHeadersToObject(res.headers);

    const ab = await res.arrayBuffer();
    const bytes = new Uint8Array(ab);
    if (bytes.length === 0) {
      respBodyBytes = Buffer.alloc(0);
      isBase64 = false;
    } else if (isProbablyUtf8(bytes)) {
      respBodyBytes = Buffer.from(bytes);
      isBase64 = false;
    } else {
      respBodyBytes = Buffer.from(bytes);
      isBase64 = true;
    }
  } catch (e) {
    status = 502;
    respHeaders = { "content-type": "text/plain; charset=utf-8" };
    respBodyBytes = Buffer.from(`Upstream fetch error: ${e?.message ?? String(e)}`, "utf8");
    isBase64 = false;
  }

  const envelope = {
    type: "response",
    id,
    response: {
      status,
      headers: respHeaders,
      body: isBase64 ? respBodyBytes.toString("base64") : respBodyBytes.toString("utf8"),
      isBase64,
    },
  };

  try {
    ws.send(JSON.stringify(envelope));
  } catch (e) {
    console.error("failed to send response", e);
  }
}

async function runForever() {
  while (true) {
    console.log(`connecting bridge: ${BRIDGE_WS_URL}`);
    const ws = new WebSocket(BRIDGE_WS_URL);

    const opened = await new Promise((resolve) => {
      ws.once("open", () => resolve(true));
      ws.once("error", () => resolve(false));
    });

    if (!opened) {
      try { ws.terminate(); } catch {}
      console.log(`connect failed, retrying in ${RECONNECT_MS}ms`);
      await sleep(RECONNECT_MS);
      continue;
    }

    console.log(`connected, forwarding to ${TARGET_HTTP_BASE}`);

    const closed = new Promise((resolve) => {
      ws.once("close", (code, reason) => {
        console.log(`bridge closed code=${code} reason=${reason?.toString?.() ?? ""}`);
        resolve();
      });
      ws.once("error", (err) => {
        console.log(`bridge error: ${err?.message ?? String(err)}`);
      });
    });

    ws.on("message", (data) => {
      let obj;
      try {
        obj = JSON.parse(data.toString("utf8"));
      } catch {
        return;
      }
      if (obj?.type !== "request") return;
      handleBridgeRequest(ws, obj);
    });

    await closed;
    console.log(`reconnecting in ${RECONNECT_MS}ms`);
    await sleep(RECONNECT_MS);
  }
}

runForever().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});