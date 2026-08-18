# WCS MCP IAM Boundary

## Purpose

Phase 4 adds the business-system side of the double-authorization boundary. VOL.AI decides which MCP tools are visible to the current Agent invocation; WCS independently authenticates every request to its `/mcp` endpoint.

## Fail-closed configuration

WCS MCP remains disabled by default. When `Mcp:Enabled=true`, JWT Authority and Audience are mandatory:

```json
{
  "Mcp": {
    "Enabled": true,
    "Route": "/mcp",
    "Authentication": {
      "Authority": "<actual-enterprise-identity-authority>",
      "Audience": "<actual-wcs-or-industrial-api-audience>",
      "RequireHttpsMetadata": true
    }
  }
}
```

No Authority/Audience value is invented in source control. An enabled MCP endpoint without either value fails startup configuration validation.

## Authorization path

```text
User / service identity
  -> VOL.AI IAM claims
  -> VOL.AI Tool Resolver / Permission / Risk filter
  -> Microsoft Agent Framework sees limited native MCP tools
  -> authenticated MCP request
  -> WCS /mcp JWT validation
  -> WCS read-only MCP tool
  -> IStateCenter
```

Phase 4 WCS tools remain read-only. Future write tools must add tool-specific WCS authorization/domain validation and the Phase 8 Workflow/HITL boundary; endpoint authentication alone is never sufficient for state-changing operations.

## Token propagation

This branch intentionally does not hard-code token forwarding or token-exchange assumptions. A token issued for VOL.AI might not be valid for WCS if the audiences differ. Production cross-application SSO must use the actual enterprise IAM contract (for example a supported token-exchange/managed authorization flow) and must never commit bearer tokens to configuration.

## Validation

Deterministic tests verify that enabling MCP requires Authority and Audience, while a disabled MCP endpoint requires no IAM configuration.

Exact-head build/test and a real authenticated `tools/list` / `tools/call` smoke test remain **Deferred** until CI/runtime and the real IAM configuration are available.
