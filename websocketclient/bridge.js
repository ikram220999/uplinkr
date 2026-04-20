import WebSocket from "ws";
import { randomInt } from "crypto";

const ADJECTIVES = [
  "amber","azure","bold","brave","bright","calm","cedar","chill","clean","clear",
  "cloud","coral","crisp","cyan","dark","dawn","deep","dusk","dusty","early",
  "east","ember","faint","fast","fierce","firm","flash","flat","fleet","fresh",
  "frost","gold","grand","green","grey","gust","hazy","high","hollow","icy",
  "jade","keen","large","late","light","lime","lofty","lone","loud","lunar",
  "mellow","mild","mint","misty","muted","navy","neat","noble","north","oak",
  "ocean","olive","open","pale","pine","plain","polar","prime","proud","pure",
  "quiet","rapid","raw","red","rich","rigid","risen","rocky","rose","rough",
  "round","royal","ruby","rusty","sage","sandy","sharp","silent","silver","sleek",
  "slim","slow","small","smart","smoky","snowy","soft","solar","solid","south",
  "spare","stark","steel","still","stone","storm","strong","sunny","swift","tall",
  "teal","thin","tidal","timber","tiny","tough","true","urban","vast","violet",
  "vivid","warm","west","white","wide","wild","windy","winter","wise","young"
];

const NOUNS = [
  "arc","ash","bay","beam","bear","bird","blade","bloom","bolt","brook",
  "brush","canyon","cave","cedar","cliff","cloud","coast","comet","coral","creek",
  "crest","crow","dale","dawn","deer","delta","dew","dome","dove","dune",
  "dust","eagle","echo","elm","ember","fern","field","finch","fjord","flame",
  "flare","fleet","flint","fog","ford","forest","fox","frost","gale","glade",
  "glen","grove","gull","hawk","heath","hill","hollow","horizon","hound","isle",
  "ivy","jay","kite","lake","lark","leaf","ledge","light","lily","lion",
  "log","lynx","maple","marsh","mast","meadow","mesa","mist","moon","moss",
  "moth","mount","oak","orbit","otter","owl","peak","pine","plain","pond",
  "pool","quail","rain","raven","reed","reef","ridge","rift","river","robin",
  "rock","root","rose","rush","sage","sand","seal","shadow","shore","sky",
  "slate","slope","snow","spark","spring","star","stem","stone","storm","stream",
  "sun","surf","swift","thorn","tide","timber","trail","vale","vine","wave",
  "wren","wolf","wood","yard"
];

function generateTunnelId() {
  const adj = ADJECTIVES[randomInt(ADJECTIVES.length)];
  const noun = NOUNS[randomInt(NOUNS.length)];
  return `${adj}-${noun}`;
}

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

function isProbablyUtf8(bytes) {
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

    if (lower === "accept-encoding") continue;

    out[key] = String(v);
  }
  return out;
}

function responseHeadersToObject(headers) {
  const obj = {};
  for (const [k, v] of headers.entries()) {
    obj[k] = v;
  }
  return obj;
}

class BridgeClient {
  constructor(bridgeWsUrl, targetHttpBase, callbacks, tunnelId) {
    this.bridgeWsUrl = bridgeWsUrl;
    this.targetHttpBase = targetHttpBase;
    this.reconnectMs = 1000;
    this.callbacks = callbacks || {};
    this.tunnelId = tunnelId;
    this.ws = null;
    this.shouldRun = true;
  }

  stop() {
    this.shouldRun = false;
    if (this.ws) {
      try {
        this.ws.terminate();
      } catch {}
    }
  }

  async handleBridgeRequest(ws, msg) {
    const id = msg?.id;
    const req = msg?.request;

    if (!id || !req || typeof req !== "object") return;

    const method = (req.method ?? "GET").toUpperCase();
    const path = req.path ?? "/";
    const url = new URL(path, this.targetHttpBase).toString();

    if (this.callbacks.onRequest) {
      this.callbacks.onRequest({ id, method, path });
    }

    const startMs = Date.now();

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
      if (this.callbacks.onError) this.callbacks.onError(`failed to send response: ${e.message}`);
    }

    const durationMs = Date.now() - startMs;
    if (this.callbacks.onResponse) {
      this.callbacks.onResponse({ id, method, path, status, durationMs });
    }
  }

  async runForever() {
    while (this.shouldRun) {
      let ws;
      try {
        ws = new WebSocket(this.bridgeWsUrl);
      } catch (e) {
        if (this.callbacks.onError) this.callbacks.onError(`Invalid WS URL: ${this.bridgeWsUrl} - ${e.message}`);
        await sleep(this.reconnectMs);
        continue;
      }
      this.ws = ws;

      const opened = await new Promise((resolve) => {
        ws.once("open", () => resolve(true));
        ws.once("error", () => resolve(false));
      });

      if (!opened) {
        try { ws.terminate(); } catch {}
        if (this.callbacks.onError) this.callbacks.onError(`Connection failed. Retrying in ${this.reconnectMs}ms...`);
        await sleep(this.reconnectMs);
        continue;
      }

      // Send the tunnelId to register the connection with the server
      if (this.tunnelId) {
        ws.send(JSON.stringify({ type: "register", tunnelId: this.tunnelId }));
      }

      if (this.callbacks.onConnect) this.callbacks.onConnect();

      const closed = new Promise((resolve) => {
        ws.once("close", (code, reason) => {
          resolve();
        });
        ws.once("error", (err) => {
          if (this.callbacks.onError) this.callbacks.onError(`Bridge error: ${err?.message ?? String(err)}`);
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
        this.handleBridgeRequest(ws, obj);
      });

      await closed;
      
      if (this.callbacks.onClose) this.callbacks.onClose();
      
      if (!this.shouldRun) break;
      await sleep(this.reconnectMs);
    }
  }
}

export { BridgeClient, generateTunnelId };
