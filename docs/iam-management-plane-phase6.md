# WCS IAM management plane (Phase 6)

## Goal

Phase 6 protects **human WCS administration** with Industrial IAM without placing IAM in
the WCS runtime hot path.

```text
Human / management client
  -> YARP
  -> Wcs.Host management API
  -> IAM JWT + SystemAccess(WCS)
  -> canonical wcs.* permission
  -> existing TransportGovernance
  -> audit / command execution

PLC / tag polling / StateCenter / EventBus
TaskScheduler / TaskOrchestrator / ResourceLock
DeadlockDetector / EMS / RGV automatic dispatch
  -> WCS runtime only
  -> no IAM request
```

This boundary is mandatory. IAM unavailability may prevent a new human management action,
but must not stop automatic transport execution already running inside WCS.

## 1. Why WCS does not use the MES/IoT Shadow pattern

MES and IoTSharp both have a real local user/role entitlement source that can be compared
against IAM. Wcs.Host does not. Its legacy management authorization is carried in request
claims and evaluated through `TransportPermissions`; `Wcs_ShadowUser` is only a mapping
table and is not a user/role source of truth.

Creating `WCS:<iam-guid>` users and calling that a Local shadow result would therefore
produce meaningless comparisons.

Phase 6 uses:

```text
Preparation -> Centralized management plane
```

rather than pretending a Local/Central Shadow comparison exists.

The WCS runtime remains autonomous in both modes.

## 2. Preparation

Before switching human management authentication, keep the normal WCS runtime configuration
and enable only resource sync. The repository includes `appsettings.IamPrepare.json` for
installations that use the ASP.NET environment profile:

```text
DOTNET_ENVIRONMENT=IamPrepare
```

Equivalent environment overrides may be used instead if changing the hosting environment is
undesirable:

```text
Security__Authentication__Mode=Local
Security__Authorization__Mode=Local
Security__ResourceSync__Enabled=true
Security__ResourceSync__SystemCode=WCS
```

The runtime workers keep the same behavior.

## 3. WCS ResourceSync service secret

Configure the same strong secret on IAM and WCS deployment configuration:

IAM:

```text
Iam__BootstrapWcsClientSecret=<secret>
```

WCS:

```text
Security__ResourceSync__ClientSecret=<same-secret>
```

Do not commit the real secret.

## 4. Canonical permission catalog

Phase 6 replaces the old UI-oriented codes such as `WCS.Task.Edit` with stable capabilities:

```text
wcs.administration.view
wcs.operation.manage
wcs.configuration.change
wcs.task.reassign
wcs.traffic.force-release
wcs.vehicle.override-low-battery
wcs.vehicle.manual-command
wcs.plc.write-signal
wcs.recovery.resolve-conflict
wcs.command.retry-compensation
wcs.operation.approve-critical
```

The old WCS codes remain only in the local mapper for migration compatibility. New API
attributes use canonical codes.

## 5. Existing TransportGovernance remains authoritative for dangerous operations

`TransportAdministrationController` performs the platform permission pre-check. Immediately
before a human mutation enters existing transport governance, the controller projects the
specific IAM `wcs.*` capabilities granted to that user back to their existing
`TransportPermissions` equivalents for the current request.

Example:

```text
IAM:
  wcs.traffic.force-release

request-only compatibility claim:
  TransportPermissions.ForceReleaseTraffic

TransportGovernance:
  existing BeginExecution / approval / audit rules
```

No compatibility claim is persisted and no runtime worker receives one.

## 6. Suggested IAM roles

These are suggestions, not hard-coded authorization logic:

### WCS_VIEWER

```text
wcs.administration.view
```

### WCS_OPERATOR

```text
wcs.administration.view
wcs.operation.manage
wcs.task.reassign
```

### WCS_ENGINEER

```text
wcs.administration.view
wcs.operation.manage
wcs.configuration.change
wcs.vehicle.override-low-battery
wcs.recovery.resolve-conflict
wcs.command.retry-compensation
```

### WCS_SUPERVISOR

```text
wcs.administration.view
wcs.operation.manage
wcs.operation.approve-critical
wcs.traffic.force-release
wcs.vehicle.manual-command
wcs.plc.write-signal
```

Do not treat role names as API authorization contracts. APIs depend on permissions only.

## 7. SystemAccess

Every IAM user allowed to reach the WCS management plane must have:

```text
SystemCode = WCS
Enabled    = true
```

`RequireSystemAccess=true` is fail-closed for human requests.

## 8. Centralized management profile

After IAM resources/roles/system access are prepared, enable:

```text
DOTNET_ENVIRONMENT=Centralized
```

or equivalent `Security__...` deployment overrides.

Expected security behavior:

```text
Authentication = Centralized
Authorization  = Centralized
SystemCode     = WCS
RequireSystemAccess = true
```

`WcsShadowUserResolver` no longer writes synthetic users to the runtime database. It supplies
only a transient compatibility identity because WCS has no business user record to preserve.

## 9. Audit

Human mutations continue through existing `TransportGovernance` audit/journal services.
The management controller also records an application log containing:

```text
Action
TargetId
UserId
GlobalUserId
Success
TraceId
Detail
```

Request bodies are not copied into the generic audit log.

High-risk actions include:

```text
configuration changes
force release traffic
manual vehicle / RGV commands
PLC writes
recovery conflict resolution
command compensation
critical-operation approval
```

## 10. Desktop safety

The pre-existing Avalonia login implementation contained a temporary bypass that invoked
`LoginSuccess` without validating credentials. Phase 6 removes that bypass. The existing
local/MES token flow must now return a real token before the desktop enters the main window.

A native `industrial-desktop` IAM Authorization Code + PKCE flow is still a separate client
migration task. Do not reintroduce the login bypass while that work is pending.

## 11. IAM outage test

The key Phase 6 acceptance test is not merely an HTTP 401/403 test.

While WCS is actively running in simulator or site mode:

1. stop IAM or block Wcs.Host -> IAM connectivity;
2. confirm new protected human administration requests fail closed;
3. confirm PLC/tag polling stays active;
4. confirm StateCenter/event processing stays active;
5. confirm task execution and automatic EMS/RGV dispatch stay active;
6. restore IAM;
7. confirm human management requests recover without restarting the WCS runtime.

## 12. Branch note

At Phase 6 start, `Dev_IAM` and `develop` were already diverged. IAM work intentionally stays
on `Dev_IAM`; merging/rebasing runtime changes from `develop` is a separate branch-integration
task and must not be mixed into the security rollout.
