#!/usr/bin/env python3
"""Fail-closed validator for externally produced S9 real-HIL evidence.

This script validates evidence already produced by a site-owned/self-hosted HIL runner.
It does not connect to PLC/RGV devices and cannot create real-HIL evidence itself.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path

SHA40 = re.compile(r"^[0-9a-fA-F]{40}$")
SHA256 = re.compile(r"^[0-9a-fA-F]{64}$")
SCHEMA = "wcs-s9-hil-evidence/v1"


def fail(message: str) -> None:
    raise SystemExit(f"S9 HIL evidence validation failed: {message}")


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def require_text(value: object, field: str) -> str:
    require(isinstance(value, str) and bool(value.strip()), f"{field} is required")
    return value.strip()


def require_sha256(value: object, field: str) -> str:
    text = require_text(value, field)
    require(bool(SHA256.fullmatch(text)), f"{field} must be a 64-character SHA-256 digest")
    return text.lower()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--bundle", required=True)
    parser.add_argument("--expected-head", required=True)
    parser.add_argument("--expected-bench", required=True)
    parser.add_argument("--expected-session", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    manifest_path = Path(args.manifest)
    bundle_path = Path(args.bundle)
    output_path = Path(args.output)
    require(manifest_path.is_file(), f"manifest not found: {manifest_path}")
    require(bundle_path.is_file(), f"evidence bundle not found: {bundle_path}")
    require(bool(SHA40.fullmatch(args.expected_head)), "expected head must be an exact 40-character Git SHA")

    try:
        data = json.loads(manifest_path.read_text(encoding="utf-8"))
    except Exception as exc:  # noqa: BLE001 - fail closed on any malformed external manifest
        fail(f"manifest is not valid UTF-8 JSON: {exc}")

    require(data.get("schemaVersion") == SCHEMA, f"schemaVersion must equal {SCHEMA}")
    require(require_text(data.get("sessionId"), "sessionId") == args.expected_session, "sessionId does not match workflow input")
    require(require_text(data.get("benchId"), "benchId") == args.expected_bench, "benchId does not match workflow input")

    head_sha = require_text(data.get("headSha"), "headSha")
    require(bool(SHA40.fullmatch(head_sha)), "headSha must be an exact 40-character Git SHA")
    require(head_sha.lower() == args.expected_head.lower(), "manifest headSha does not match expected exact head")

    require(data.get("runnerKind") == "SelfHostedHil", "runnerKind must be SelfHostedHil")
    labels = data.get("runnerLabels")
    require(isinstance(labels, list), "runnerLabels must be an array")
    label_values = {str(label).lower() for label in labels}
    require("self-hosted" in label_values and "wcs-hil" in label_values, "runnerLabels must contain self-hosted and wcs-hil")
    require(data.get("realHardwareConnected") is True, "realHardwareConnected must be true")
    require(data.get("productionNetworkIsolated") is True, "productionNetworkIsolated must be true")
    require(data.get("usesProductionCredentials") is False, "usesProductionCredentials must be false")

    operator = require_text(data.get("operator"), "operator")
    safety_approver = require_text(data.get("safetyApprover"), "safetyApprover")
    require(operator.casefold() != safety_approver.casefold(), "operator and safetyApprover must be different people")
    require_text(data.get("changeTicket"), "changeTicket")
    require_text(data.get("maintenanceWindowId"), "maintenanceWindowId")

    preflight = data.get("preflight")
    require(isinstance(preflight, dict), "preflight object is required")
    for field in (
        "emergencyStopVerified",
        "mechanicalInterlocksVerified",
        "guardingVerified",
        "networkIsolationVerified",
        "maintenanceModeVerified",
        "operatorAreaClear",
    ):
        require(preflight.get(field) is True, f"preflight.{field} must be true")

    steps = data.get("steps")
    require(isinstance(steps, list) and len(steps) > 0, "steps must contain at least one real-HIL result")
    seen_steps: set[str] = set()
    for index, step in enumerate(steps):
        require(isinstance(step, dict), f"steps[{index}] must be an object")
        step_id = require_text(step.get("stepId"), f"steps[{index}].stepId")
        require(step_id not in seen_steps, f"duplicate stepId: {step_id}")
        seen_steps.add(step_id)
        require_text(step.get("assetId"), f"steps[{index}].assetId")
        require(step.get("result") == "Passed", f"step {step_id} must have result=Passed")
        require(step.get("realHardwareObserved") is True, f"step {step_id} must have realHardwareObserved=true")
        require_sha256(step.get("evidenceSha256"), f"steps[{index}].evidenceSha256")

    acceptance = data.get("acceptance")
    require(isinstance(acceptance, dict), "acceptance object is required")
    require(acceptance.get("protocolValidated") is True, "acceptance.protocolValidated must be true")
    require(acceptance.get("mechanicalSafetyAccepted") is True, "acceptance.mechanicalSafetyAccepted must be true")
    require(acceptance.get("siteAccepted") is True, "acceptance.siteAccepted must be true")
    require_text(acceptance.get("acceptedBy"), "acceptance.acceptedBy")
    require_sha256(acceptance.get("protocolEvidenceSha256"), "acceptance.protocolEvidenceSha256")
    require_sha256(acceptance.get("mechanicalSafetyEvidenceSha256"), "acceptance.mechanicalSafetyEvidenceSha256")
    require_sha256(acceptance.get("siteAcceptanceEvidenceSha256"), "acceptance.siteAcceptanceEvidenceSha256")

    expected_bundle_sha = require_sha256(data.get("evidenceBundleSha256"), "evidenceBundleSha256")
    actual_bundle_sha = sha256_file(bundle_path)
    require(actual_bundle_sha == expected_bundle_sha, "evidence bundle SHA-256 does not match manifest")

    manifest_sha = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
    summary = {
        "schemaVersion": SCHEMA,
        "sessionId": args.expected_session,
        "benchId": args.expected_bench,
        "headSha": args.expected_head.lower(),
        "runnerKind": "SelfHostedHil",
        "realHardwareConnected": True,
        "stepCount": len(steps),
        "allStepsPassedWithRealHardware": True,
        "protocolValidated": True,
        "mechanicalSafetyAccepted": True,
        "siteAccepted": True,
        "manifestSha256": manifest_sha,
        "evidenceBundleSha256": actual_bundle_sha,
        "accepted": True,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(summary, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(summary, sort_keys=True))


if __name__ == "__main__":
    main()
