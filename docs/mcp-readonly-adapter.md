# WCS Read-only MCP Adapter

## Purpose

Expose a deliberately small read-only WCS capability surface to VOL.AI through the official MCP C# SDK. The adapter is hosted in `Wcs.Host` and reads the existing in-memory `IStateCenter` system truth. It does not duplicate WCS domain logic.

## Endpoint

The endpoint is disabled by default. Enable it only in an explicitly configured environment:

```json
{
  "Mcp": {
    "Enabled": true,
    "Route": "/mcp"
  }
}
```

`ModelContextProtocol.AspNetCore` hosts the endpoint with stateless Streamable HTTP. The actual host/base URL is deployment-specific and is intentionally not stored in this document.

Until VOL.AI Phase 4 IAM/Risk integration is complete, do not expose this endpoint to an untrusted network. Phase 2 tools are read-only, but network authorization is still a separate production requirement.

## Published tools

### `wcs_get_device_state`

Input: stable `deviceId`.

Returns only:
- device id
- status
- current position
- last update time

It does not expose the arbitrary `DeviceState.Properties` dictionary or PLC blocks.

### `wcs_get_active_tasks`

Input: required `limit` (clamped to 1..100).

Returns active task count and a bounded list containing:
- task id
- status
- priority
- route id
- created/start/end timestamps

It deliberately omits `TaskRuntime.Parameters`.

### `wcs_get_active_alarms`

Input: required `limit` (clamped to 1..100).

Returns active/acknowledged alarms with business-readable alarm metadata. It cannot acknowledge, recover, mask, suppress, or change an alarm.

### `wcs_get_system_overview`

Returns aggregate StateCenter counts only:
- devices
- non-offline devices
- error devices
- active tasks
- active alarms
- tracked objects

No raw PLC/state dictionaries are included.

## Boundary

```text
MCP Tool
  -> IStateCenter read API
  -> WCS in-memory system truth
```

Forbidden in Phase 2:

```text
MCP -> CommandBus
MCP -> Device command
MCP -> PLC write
MCP -> SQL mutation
MCP -> Task creation/cancellation
MCP -> Alarm acknowledge/recover
```

The MCP package is referenced only by `Wcs.Host`. `Wcs.Core` and `Wcs.Application` remain independent of MCP.

## Validation

Direct deterministic tests are located under `Wcs.Core.Tests/Mcp`. They validate the adapter's minimal read-only DTO mapping without starting PLC connections or a live MCP server.

Exact-head restore/build/test and a live Streamable HTTP `tools/list` / `tools/call` smoke test remain Deferred until CI or a .NET execution environment runs this branch.
