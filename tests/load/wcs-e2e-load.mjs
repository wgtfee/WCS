import fs from "node:fs";
import path from "node:path";
import { performance } from "node:perf_hooks";

const recordSeparator = "\u001e";

function intFromEnv(name, fallback, minimum = 1) {
  const parsed = Number.parseInt(process.env[name] ?? "", 10);
  return Number.isFinite(parsed) ? Math.max(minimum, parsed) : fallback;
}

function numberFromEnv(name, fallback, minimum = 0) {
  const parsed = Number.parseFloat(process.env[name] ?? "");
  return Number.isFinite(parsed) ? Math.max(minimum, parsed) : fallback;
}

const config = {
  baseUrl: (process.env.BASE_URL ?? "http://127.0.0.1:5080").replace(/\/+$/, ""),
  durationSeconds: intFromEnv("DURATION_SECONDS", 60),
  httpConcurrency: intFromEnv("HTTP_CONCURRENCY", 16),
  signalRConnections: intFromEnv("SIGNALR_CONNECTIONS", 50, 0),
  writeIntervalMs: intFromEnv("WRITE_INTERVAL_MS", 1000),
  requestTimeoutMs: intFromEnv("REQUEST_TIMEOUT_MS", 5000),
  maximumErrorRatePercent: numberFromEnv("MAX_ERROR_RATE_PERCENT", 1),
  maximumP95Milliseconds: numberFromEnv("MAX_P95_MS", 1000),
  minimumSignalRMessages: intFromEnv("MIN_SIGNALR_MESSAGES", 1, 0),
  hostProcessId: intFromEnv("HOST_PID", 0, 0),
  outputPath: process.env.OUTPUT_PATH ?? "artifacts/e2e-load-results.json"
};

const targetHost = new URL(config.baseUrl).hostname;
const loopbackHosts = new Set(["127.0.0.1", "localhost", "::1", "[::1]"]);
if (!loopbackHosts.has(targetHost) && process.env.ALLOW_REMOTE_TARGET !== "true") {
  throw new Error(
    `Refusing to load-test non-loopback target ${targetHost}. ` +
    "Set ALLOW_REMOTE_TARGET=true only for an approved isolated test environment.");
}

const readEndpoints = [
  "/",
  "/health/live",
  "/api/overview",
  "/api/devices",
  "/api/devices/db",
  "/api/tasks",
  "/api/transport/observability/summary",
  "/api/transport/observability/metrics"
];

const latencies = [];
const endpointResults = new Map();
const signalR = {
  requestedConnections: config.signalRConnections,
  connectedConnections: 0,
  completedSubscriptions: 0,
  serverMessages: 0,
  messagesByTarget: {},
  errors: []
};
const memory = {
  samples: 0,
  initialRssMb: null,
  finalRssMb: null,
  peakRssMb: null
};
const validation = {};
const failures = [];
const sockets = [];

let totalRequests = 0;
let failedRequests = 0;
let loadStartedAt = 0;
let loadCompletedAt = 0;
let loadStartedAtUtc = null;

function sleep(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

function endpointStats(label) {
  if (!endpointResults.has(label)) {
    endpointResults.set(label, {
      requests: 0,
      failures: 0,
      statusCodes: {}
    });
  }
  return endpointResults.get(label);
}

function recordRequest(label, statusCode, elapsedMilliseconds, succeeded) {
  totalRequests++;
  latencies.push(elapsedMilliseconds);
  const endpoint = endpointStats(label);
  endpoint.requests++;
  const statusKey = String(statusCode ?? "network-error");
  endpoint.statusCodes[statusKey] = (endpoint.statusCodes[statusKey] ?? 0) + 1;
  if (!succeeded) {
    failedRequests++;
    endpoint.failures++;
  }
}

async function measuredFetch(relativePath, options = {}, label = relativePath) {
  const startedAt = performance.now();
  try {
    const response = await fetch(new URL(relativePath, config.baseUrl), {
      ...options,
      signal: AbortSignal.timeout(config.requestTimeoutMs)
    });
    const body = await response.arrayBuffer();
    recordRequest(label, response.status, performance.now() - startedAt, response.ok);
    return {
      ok: response.ok,
      status: response.status,
      body
    };
  } catch (error) {
    recordRequest(label, null, performance.now() - startedAt, false);
    return {
      ok: false,
      status: null,
      error: error instanceof Error ? error.message : String(error),
      body: new ArrayBuffer(0)
    };
  }
}

function parseJsonBody(result) {
  if (!result.ok) {
    throw new Error(`HTTP ${result.status ?? "network-error"}`);
  }
  return JSON.parse(new TextDecoder().decode(result.body));
}

function percentile(values, percentileValue) {
  if (values.length === 0) {
    return 0;
  }
  const sorted = [...values].sort((left, right) => left - right);
  const index = Math.ceil(percentileValue * sorted.length) - 1;
  return sorted[Math.max(0, Math.min(index, sorted.length - 1))];
}

function readHostRssMb() {
  if (config.hostProcessId <= 0) {
    return null;
  }
  try {
    const status = fs.readFileSync(`/proc/${config.hostProcessId}/status`, "utf8");
    const match = /^VmRSS:\s+(\d+)\s+kB$/m.exec(status);
    return match ? Number.parseInt(match[1], 10) / 1024 : null;
  } catch {
    return null;
  }
}

function sampleMemory() {
  const rssMb = readHostRssMb();
  if (rssMb === null) {
    return;
  }
  memory.samples++;
  memory.initialRssMb ??= rssMb;
  memory.finalRssMb = rssMb;
  memory.peakRssMb = memory.peakRssMb === null
    ? rssMb
    : Math.max(memory.peakRssMb, rssMb);
}

function toText(data) {
  if (typeof data === "string") {
    return Promise.resolve(data);
  }
  if (data instanceof ArrayBuffer) {
    return Promise.resolve(new TextDecoder().decode(data));
  }
  if (ArrayBuffer.isView(data)) {
    return Promise.resolve(new TextDecoder().decode(data));
  }
  if (data && typeof data.text === "function") {
    return data.text();
  }
  return Promise.resolve(String(data));
}

async function openSignalRConnection(index) {
  const negotiation = await fetch(
    new URL("/wcs/negotiate?negotiateVersion=1", config.baseUrl),
    {
      method: "POST",
      signal: AbortSignal.timeout(config.requestTimeoutMs)
    });
  if (!negotiation.ok) {
    throw new Error(`SignalR negotiate returned HTTP ${negotiation.status}`);
  }
  const negotiationBody = await negotiation.json();
  const connectionToken = negotiationBody.connectionToken ?? negotiationBody.connectionId;
  if (!connectionToken) {
    throw new Error("SignalR negotiate did not return a connection token");
  }

  const webSocketUrl = new URL("/wcs", config.baseUrl);
  webSocketUrl.protocol = webSocketUrl.protocol === "https:" ? "wss:" : "ws:";
  webSocketUrl.searchParams.set("id", connectionToken);
  const socket = new WebSocket(webSocketUrl);
  const invocationId = `subscribe-${index}`;
  let buffer = "";
  let handshakeCompleted = false;
  let subscriptionCompleted = false;

  await new Promise((resolve, reject) => {
    const timeout = setTimeout(
      () => reject(new Error("SignalR handshake timed out")),
      config.requestTimeoutMs);

    function fail(error) {
      clearTimeout(timeout);
      try {
        socket.close();
      } catch {
        // Best-effort cleanup for a failed handshake.
      }
      reject(error);
    }

    socket.addEventListener("open", () => {
      socket.send(`${JSON.stringify({ protocol: "json", version: 1 })}${recordSeparator}`);
    });
    socket.addEventListener("error", () => {
      fail(new Error("SignalR WebSocket transport failed"));
    });
    socket.addEventListener("close", event => {
      if (!subscriptionCompleted) {
        fail(new Error(`SignalR closed before subscription completed (${event.code})`));
      }
    });
    socket.addEventListener("message", async event => {
      buffer += await toText(event.data);
      const frames = buffer.split(recordSeparator);
      buffer = frames.pop() ?? "";
      for (const frame of frames) {
        if (!frame) {
          continue;
        }
        let message;
        try {
          message = JSON.parse(frame);
        } catch (error) {
          fail(new Error(
            `SignalR returned invalid JSON: ${error instanceof Error ? error.message : String(error)}`));
          return;
        }
        if (!handshakeCompleted) {
          if (message.error) {
            fail(new Error(`SignalR handshake failed: ${message.error}`));
            return;
          }
          handshakeCompleted = true;
          socket.send(`${JSON.stringify({
            type: 1,
            invocationId,
            target: "SubscribeAlarm",
            arguments: []
          })}${recordSeparator}`);
          continue;
        }
        if (message.type === 1) {
          signalR.serverMessages++;
          const target = message.target ?? "unknown";
          signalR.messagesByTarget[target] = (signalR.messagesByTarget[target] ?? 0) + 1;
        }
        if (message.type === 3 && message.invocationId === invocationId) {
          if (message.error) {
            fail(new Error(`SignalR subscription failed: ${message.error}`));
            return;
          }
          subscriptionCompleted = true;
          signalR.completedSubscriptions++;
          clearTimeout(timeout);
          resolve();
        }
      }
    });
  });

  signalR.connectedConnections++;
  return socket;
}

async function openSignalRConnections() {
  const batchSize = 20;
  for (let start = 0; start < config.signalRConnections; start += batchSize) {
    const count = Math.min(batchSize, config.signalRConnections - start);
    const results = await Promise.allSettled(
      Array.from({ length: count }, (_, offset) =>
        openSignalRConnection(start + offset)));
    for (const result of results) {
      if (result.status === "fulfilled") {
        sockets.push(result.value);
      } else {
        signalR.errors.push(
          result.reason instanceof Error ? result.reason.message : String(result.reason));
      }
    }
  }
}

async function runReadWorker(workerIndex, endsAt) {
  let requestIndex = workerIndex;
  while (Date.now() < endsAt) {
    const endpoint = readEndpoints[requestIndex % readEndpoints.length];
    await measuredFetch(endpoint);
    requestIndex += config.httpConcurrency;
  }
}

async function runWriteWorker(endsAt) {
  let sequence = 0;
  while (Date.now() < endsAt) {
    sequence++;
    await measuredFetch(
      "/api/tasks",
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          deviceId: `LOAD-CV-${(sequence % 4) + 1}`,
          routeId: "LOAD-CV→ASRS01",
          priority: 2,
          parameters: {
            source: "e2e-load",
            sequence
          }
        })
      },
      "POST /api/tasks");
    await sleep(config.writeIntervalMs);
  }
}

async function collectValidation() {
  const liveDevices = parseJsonBody(await measuredFetch("/api/devices", {}, "validation /api/devices"));
  const persistedDevices = parseJsonBody(
    await measuredFetch("/api/devices/db", {}, "validation /api/devices/db"));
  const persistedTasks = parseJsonBody(
    await measuredFetch("/api/tasks/db", {}, "validation /api/tasks/db"));
  const liveness = await measuredFetch("/health/live", {}, "validation /health/live");

  validation.liveDeviceCount = Array.isArray(liveDevices) ? liveDevices.length : 0;
  validation.persistedDeviceCount = Array.isArray(persistedDevices) ? persistedDevices.length : 0;
  validation.persistedTaskCount = Array.isArray(persistedTasks) ? persistedTasks.length : 0;
  validation.hostAliveAfterLoad = liveness.ok;
}

function buildResult() {
  const elapsedSeconds = Math.max(0.001, (loadCompletedAt - loadStartedAt) / 1000);
  const errorRatePercent = totalRequests === 0
    ? 100
    : failedRequests * 100 / totalRequests;
  const result = {
    startedAtUtc: loadStartedAtUtc ?? new Date().toISOString(),
    completedAtUtc: new Date().toISOString(),
    configuration: config,
    http: {
      totalRequests,
      failedRequests,
      errorRatePercent: Number(errorRatePercent.toFixed(3)),
      requestsPerSecond: Number((totalRequests / elapsedSeconds).toFixed(2)),
      p50Milliseconds: Number(percentile(latencies, 0.50).toFixed(2)),
      p95Milliseconds: Number(percentile(latencies, 0.95).toFixed(2)),
      p99Milliseconds: Number(percentile(latencies, 0.99).toFixed(2)),
      maximumMilliseconds: Number(
        latencies.reduce((maximum, value) => Math.max(maximum, value), 0).toFixed(2)),
      endpoints: Object.fromEntries(endpointResults)
    },
    signalR,
    memory: {
      samples: memory.samples,
      initialRssMb: memory.initialRssMb === null ? null : Number(memory.initialRssMb.toFixed(2)),
      finalRssMb: memory.finalRssMb === null ? null : Number(memory.finalRssMb.toFixed(2)),
      peakRssMb: memory.peakRssMb === null ? null : Number(memory.peakRssMb.toFixed(2))
    },
    validation,
    failures
  };
  return result;
}

async function main() {
  let memoryTimer;
  try {
    await openSignalRConnections();
    sampleMemory();
    memoryTimer = setInterval(sampleMemory, 1000);

    loadStartedAtUtc = new Date().toISOString();
    loadStartedAt = performance.now();
    const endsAt = Date.now() + config.durationSeconds * 1000;
    await Promise.all([
      ...Array.from(
        { length: config.httpConcurrency },
        (_, index) => runReadWorker(index, endsAt)),
      runWriteWorker(endsAt)
    ]);
    loadCompletedAt = performance.now();
    await sleep(2000);
    await collectValidation();
    sampleMemory();
  } catch (error) {
    failures.push(error instanceof Error ? error.message : String(error));
    if (loadStartedAt === 0) {
      loadStartedAt = performance.now();
    }
    loadCompletedAt = performance.now();
  } finally {
    if (memoryTimer) {
      clearInterval(memoryTimer);
    }
    for (const socket of sockets) {
      try {
        socket.close(1000, "load test completed");
      } catch {
        // Best-effort cleanup.
      }
    }
  }

  const elapsedSeconds = Math.max(0.001, (loadCompletedAt - loadStartedAt) / 1000);
  const errorRatePercent = totalRequests === 0
    ? 100
    : failedRequests * 100 / totalRequests;
  const p95Milliseconds = percentile(latencies, 0.95);
  if (totalRequests < config.httpConcurrency) {
    failures.push(`Only ${totalRequests} HTTP requests completed`);
  }
  if (errorRatePercent > config.maximumErrorRatePercent) {
    failures.push(
      `HTTP error rate ${errorRatePercent.toFixed(3)}% exceeds ${config.maximumErrorRatePercent}%`);
  }
  if (p95Milliseconds > config.maximumP95Milliseconds) {
    failures.push(
      `HTTP P95 ${p95Milliseconds.toFixed(2)}ms exceeds ${config.maximumP95Milliseconds}ms`);
  }
  if (signalR.connectedConnections !== config.signalRConnections) {
    failures.push(
      `SignalR connected ${signalR.connectedConnections}/${config.signalRConnections}`);
  }
  if (signalR.serverMessages < config.minimumSignalRMessages) {
    failures.push(
      `SignalR received ${signalR.serverMessages} messages; expected at least ${config.minimumSignalRMessages}`);
  }
  if ((validation.liveDeviceCount ?? 0) === 0) {
    failures.push("Simulated PLC produced no live device state");
  }
  if ((validation.persistedDeviceCount ?? 0) === 0) {
    failures.push("SQL Server contains no persisted simulated device state");
  }
  if ((validation.persistedTaskCount ?? 0) === 0) {
    failures.push("SQL Server contains no completed task evidence");
  }
  if (validation.hostAliveAfterLoad !== true) {
    failures.push("Host liveness check failed after load");
  }
  const taskWrites = endpointResults.get("POST /api/tasks");
  if (!taskWrites || taskWrites.requests === 0 || taskWrites.failures > 0) {
    failures.push("Task write endpoint did not complete successfully throughout the load");
  }

  const result = buildResult();
  fs.mkdirSync(path.dirname(config.outputPath), { recursive: true });
  fs.writeFileSync(config.outputPath, `${JSON.stringify(result, null, 2)}\n`, "utf8");
  console.log(JSON.stringify({
    elapsedSeconds: Number(elapsedSeconds.toFixed(2)),
    http: result.http,
    signalR: result.signalR,
    memory: result.memory,
    validation: result.validation,
    failures: result.failures
  }, null, 2));

  if (failures.length > 0) {
    process.exitCode = 1;
  }
}

await main();
process.exit(process.exitCode ?? 0);
