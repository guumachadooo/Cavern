#!/usr/bin/env python3
"""Out-of-proc Zero-Static Co-Evolution & Parity Engine (`co_evolve.py`, Wave W56.4).

Deterministic, stdlib-only (<250 ms, LC_ALL=C, sorted JSON, LF) single-pass checker &
generator enforcing zero-drift parity across 4 Cavern / CavernizeAndroid domains:
  1. `bridge_cmd`        : BridgeCommandOpcode <-> CapabilitiesCatalog <-> switch(desc.Opcode)
                           + canonical SHA-256 SchemaHash + federation.json tabNames parity.
  2. `packaging`         : .agents/federation.json -> CavernizeAndroid.csproj, AndroidManifest.xml,
                           BuildAndroid.yml, AppUpdateService.cs, LiveDiagnosticBridgeService.Commands.cs.
  3. `settings_schema`   : AppSettingsStore.cs -> generated AppSettingsStore.Schema.cs (zero-reflection
                           typed Reset table with true defaults) + 0 put-only drifted pref keys +
                           0 Activator.CreateInstance in settings_reset.
  4. `rpe_sbe_dsp_parity`: Declarative RPE <-> SBE DSP stage parity ledger + explicit RT/Export
                           exceptions + MemoryBudgetGovernor (zero filename heuristics, zero /proc/pressure).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]

FEDERATION_PATH = REPO_ROOT / ".agents" / "federation.json"
CSPROJ_PATH = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "CavernizeAndroid.csproj"
MANIFEST_PATH = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Properties" / "AndroidManifest.xml"
BUILD_YML_PATH = REPO_ROOT / ".github" / "workflows" / "BuildAndroid.yml"
UPDATE_SVC_PATH = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AppUpdateService.cs"
BRIDGE_SVC_PATH = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "LiveDiagnosticBridgeService.cs"
BRIDGE_CMD_PATH = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "LiveDiagnosticBridgeService.Commands.cs"
MAIN_BRIDGE_PATH = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.LiveBridge.cs"
SETTINGS_STORE_PATH = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AppSettingsStore.cs"
SETTINGS_SCHEMA_PATH = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AppSettingsStore.Schema.cs"
NEURAL_MODELS_PATH = REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "NeuralAudioModels.cs"
ONNX_BRIDGE_PATH = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "NativeOnnxRuntimeBridge.cs"
RPE_DIR = REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Rendering"
SBE_PATH = RPE_DIR / "StudioBatchExporter.cs"
ANDROID_ROOT = REPO_ROOT / "CavernSamples" / "CavernizeAndroid"

SECRET_TOKENS = ("encrypted", "token", "secret", "password", "apikey", "credential")

# Declarative RPE <-> SBE DSP Parity Ledger (W56.4.3)
DSP_PARITY_LEDGER: dict[str, list[dict[str, Any]]] = {
    "shared_stages": [
        {
            "id": "headphone_virtualizer",
            "rpe_tokens": ["VirtualizerFilter", "Listener.HeadphoneVirtualizer"],
            "sbe_tokens": ["VirtualizerFilter", "Listener.HeadphoneVirtualizer"],
            "note": "Canonical binaural HRTF virtualizer layout & filter pipeline.",
        },
        {
            "id": "hybrid_neural_dirac_714",
            "rpe_tokens": ["NeuralStemSpatializer.BuildHybrid714WithDiscreteLfe"],
            "sbe_tokens": ["NeuralStemSpatializer.BuildHybrid714WithDiscreteLfe"],
            "note": "Shared stateless 12-ch 7.1.4 + 120 Hz LR4 LFE builder.",
        },
        {
            "id": "spatial_choreography",
            "rpe_tokens": ["ActiveChoreoTrack", "TryGetChoreoSourceWorldPositionZeroAlloc"],
            "sbe_tokens": ["SpatialChoreoTrack", "ChoreoSourceMapper", "EvaluateStemPositionZeroAlloc"],
            "note": "Zero-alloc trajectory evaluator shared 1:1 between RT playback and Studio export.",
        },
        {
            "id": "neural_stem_spatializer",
            "rpe_tokens": ["NeuralStemSpatializer.FilterRenderableStems", "SplitLinkwitzRiley4Lfe"],
            "sbe_tokens": ["NeuralStemSpatializer", "SplitLinkwitzRiley4Lfe"],
            "note": "Stem deduplication & LR4 sub-bass crossover.",
        },
        {
            "id": "post_processing_slot6",
            "rpe_tokens": ["AutoEqPostProcessingStage", "HptfNotchPostProcessingStage", "Iso226LoudnessPostProcessingStage", "Bs1770LoudnessPostProcessingStage", "SpatialDialogPostProcessingStage"],
            "sbe_tokens": ["IPostProcessingStage", "CloneOfflinePostStages"],
            "note": "Slot 6 personalized headphone & loudness post-processing stages.",
        },
        {
            "id": "true_peak_limiter",
            "rpe_tokens": ["TruePeakLimiterPostProcessingStage"],
            "sbe_tokens": ["TruePeakLimiter"],
            "note": "4x oversampled ITU-R BS.1770 true-peak brickwall limiter.",
        },
        {
            "id": "live_vocal_chain",
            "rpe_tokens": ["VocalMixMatchStageProcessor", "Cavern.VocalChain"],
            "sbe_tokens": ["LiveVocalChainDsp"],
            "note": "W11 Live Vocal Chain DSP shared across live mic & Studio vocal take mixdown.",
        },
        {
            "id": "physical_externalization",
            "rpe_tokens": ["PhysicalExternalizationConfig"],
            "sbe_tokens": ["VirtualizerFilter.BeginStaticStateScope"],
            "note": "Physical ISM/BRIR externalization state preserved cleanly inside VirtualizerFilter scope.",
        },
        {
            "id": "parametric_upmix",
            "rpe_tokens": ["UpmixingSettings"],
            "sbe_tokens": ["UpmixingSettings"],
            "note": "Cavernize / Matrix / DirAC parametric spatial upmix configuration.",
        },
    ],
    "rt_only_exceptions": [
        {
            "id": "live_head_tracking_6dof",
            "rpe_tokens": ["IHeadTrackerProvider", "ListenerPose6DoF"],
            "justification": "Live head orientation cannot be baked into static offline exports.",
        },
        {
            "id": "live_microphone_bridge",
            "rpe_tokens": ["LiveMicrophoneBridge", "ProAudioDuplexConfig"],
            "justification": "Real-time USB/mic duplex monitor; offline Studio renders recorded takes via LiveVocalChainDsp.",
        },
        {
            "id": "transaural_xtc_speaker_route",
            "rpe_tokens": ["TransauralXtcPostProcessingStage"],
            "justification": "Hardware-route speaker crosstalk cancellation (auto-bypassed on headphones; never baked into portable exports).",
        },
        {
            "id": "clickless_crossfade",
            "rpe_tokens": ["ArmCrossfadeLocked", "crossfadeFrames"],
            "justification": "Interactive seek/toggle pop-suppression strictly for live playback transitions.",
        },
    ],
    "export_only_exceptions": [
        {
            "id": "adm_bwf_container",
            "sbe_tokens": ["Codec.ADM_BWF_Atmos", "UseDolbyAtmosAdmSubset"],
            "justification": "Offline ITU-R BS.2076 ADM BWF container serialization.",
        },
        {
            "id": "streaming_export_sink",
            "sbe_tokens": ["StudioCompressionTier", "StudioBaseOutputFormat"],
            "justification": "Offline file encoding, bit-depth formatting, and container muxing.",
        },
        {
            "id": "offline_loudness_report",
            "sbe_tokens": ["ApplyTruePeakLimiterAndMeasureInPlace"],
            "justification": "Full-file offline integrated LUFS/LRA/True-Peak certification pass.",
        },
    ],
}


def _read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def _extract_balanced(src: str, start_idx: int, open_ch: str, close_ch: str) -> tuple[str, int]:
    """Extract content inside balanced delimiters starting just AFTER open_ch."""
    depth = 1
    i = start_idx
    n = len(src)
    in_str = False
    in_char = False
    escape = False
    while i < n and depth > 0:
        ch = src[i]
        if escape:
            escape = False
            i += 1
            continue
        if in_str:
            if ch == "\\":
                escape = True
            elif ch == '"':
                in_str = False
            i += 1
            continue
        if in_char:
            if ch == "\\":
                escape = True
            elif ch == "'":
                in_char = False
            i += 1
            continue
        if ch == '"':
            in_str = True
        elif ch == "'":
            in_char = True
        elif ch == open_ch:
            depth += 1
        elif ch == close_ch:
            depth -= 1
            if depth == 0:
                return src[start_idx:i], i + 1
        i += 1
    return src[start_idx:], n


def _split_top_level_args(arg_str: str) -> list[str]:
    parts: list[str] = []
    cur: list[str] = []
    depth = 0
    in_str = False
    in_char = False
    escape = False
    for ch in arg_str:
        if escape:
            cur.append(ch)
            escape = False
            continue
        if in_str:
            cur.append(ch)
            if ch == "\\":
                escape = True
            elif ch == '"':
                in_str = False
            continue
        if in_char:
            cur.append(ch)
            if ch == "\\":
                escape = True
            elif ch == "'":
                in_char = False
            continue
        if ch == '"':
            in_str = True
            cur.append(ch)
        elif ch == "'":
            in_char = True
            cur.append(ch)
        elif ch in "([{":
            depth += 1
            cur.append(ch)
        elif ch in ")]}":
            depth = max(0, depth - 1)
            cur.append(ch)
        elif ch == "," and depth == 0:
            parts.append("".join(cur).strip())
            cur = []
        else:
            cur.append(ch)
    tail = "".join(cur).strip()
    if tail:
        parts.append(tail)
    return parts


def _escape_csharp_string(val: str) -> str:
    return val.replace("\\", "\\\\").replace('"', '\\"').replace("\r", "").replace("\n", "\\n")


def _load_federation_packaging() -> tuple[str, dict[str, Any]]:
    fed = json.loads(_read_text(FEDERATION_PATH))
    cavern = fed["repos"]["cavern"]
    return str(cavern["ownerRepo"]), dict(cavern["packaging"])


# ==================== 1. BRIDGE CMD UNIFICATION ====================


def check_bridge_cmd(packaging: dict[str, Any]) -> tuple[list[str], dict[str, Any]]:
    errors: list[str] = []
    svc_src = _read_text(BRIDGE_SVC_PATH)
    cmd_src = _read_text(BRIDGE_CMD_PATH)
    main_src = _read_text(MAIN_BRIDGE_PATH)

    # Parse enum BridgeCommandOpcode
    m_enum = re.search(r"public\s+enum\s+BridgeCommandOpcode\s*:\s*ushort\s*\{", svc_src)
    if not m_enum:
        return ["bridge_cmd: missing `public enum BridgeCommandOpcode : ushort` in LiveDiagnosticBridgeService.cs"], {}
    enum_body, _ = _extract_balanced(svc_src, m_enum.end(), "{", "}")
    opcodes: dict[str, int] = {}
    seen_nums: set[int] = set()
    for em in re.finditer(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(\d+)", enum_body):
        ename, num = em.group(1), int(em.group(2))
        if ename in opcodes:
            errors.append(f"bridge_cmd: duplicate enum member '{ename}'")
        if num <= 0 or num in seen_nums:
            errors.append(f"bridge_cmd: invalid/duplicate opcode number {num} for '{ename}'")
        opcodes[ename] = num
        seen_nums.add(num)

    # Parse CapabilitiesCatalog
    m_cat = re.search(r"CapabilitiesCatalog\s*=\s*\[", svc_src)
    if not m_cat:
        return ["bridge_cmd: missing `CapabilitiesCatalog` in LiveDiagnosticBridgeService.cs"], {}
    cat_body, _ = _extract_balanced(svc_src, m_cat.end(), "[", "]")
    catalog: list[tuple[str, int, str, int, str]] = []
    cat_enum_names: set[str] = set()
    cat_cmd_names: set[str] = set()
    entry_re = re.compile(
        r'new\(\s*BridgeCommandOpcode\.([A-Za-z0-9_]+)\s*,\s*"([^"]+)"\s*,\s*(\d+)\s*,\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)'
    )
    for cm in entry_re.finditer(cat_body):
        enum_name, cmd_name, tier_s, _summary, args_schema = (
            cm.group(1),
            cm.group(2),
            cm.group(3),
            cm.group(4),
            cm.group(5),
        )
        tier = int(tier_s)
        if enum_name not in opcodes:
            errors.append(f"bridge_cmd: CapabilitiesCatalog references unknown opcode '{enum_name}'")
            continue
        if enum_name in cat_enum_names:
            errors.append(f"bridge_cmd: duplicate opcode '{enum_name}' in CapabilitiesCatalog")
        if cmd_name in cat_cmd_names:
            errors.append(f"bridge_cmd: duplicate command name '{cmd_name}' in CapabilitiesCatalog")
        cat_enum_names.add(enum_name)
        cat_cmd_names.add(cmd_name)
        catalog.append((enum_name, opcodes[enum_name], cmd_name, tier, args_schema))

    # Parse switch (desc.Opcode) cases in LiveDiagnosticBridgeService.Commands.cs
    if "switch (desc.Opcode)" not in cmd_src:
        errors.append("bridge_cmd: ExecuteSingleCommandAsync must dispatch via `switch (desc.Opcode)`")
    switch_cases = set(re.findall(r"\bcase\s+BridgeCommandOpcode\.([A-Za-z0-9_]+)\s*:", cmd_src))

    enum_set = set(opcodes.keys())
    if enum_set != cat_enum_names:
        missing_cat = sorted(enum_set - cat_enum_names)
        extra_cat = sorted(cat_enum_names - enum_set)
        if missing_cat:
            errors.append(f"bridge_cmd: opcodes missing from CapabilitiesCatalog: {missing_cat}")
        if extra_cat:
            errors.append(f"bridge_cmd: extra opcodes in CapabilitiesCatalog: {extra_cat}")

    if enum_set != switch_cases:
        missing_sw = sorted(enum_set - switch_cases)
        extra_sw = sorted(switch_cases - enum_set)
        if missing_sw:
            errors.append(f"bridge_cmd: opcodes missing from switch(desc.Opcode): {missing_sw}")
        if extra_sw:
            errors.append(f"bridge_cmd: extra switch cases not in BridgeCommandOpcode: {extra_sw}")

    # Deterministic SchemaHash matching C# ComputeCatalogSchemaHash()
    canon_lines = [
        f"{num}|{cmd_name}|{tier}|{args_schema}\n"
        for _ename, num, cmd_name, tier, args_schema in sorted(catalog, key=lambda x: x[1])
    ]
    schema_hash = hashlib.sha256("".join(canon_lines).encode("utf-8")).hexdigest()
    if "BridgeCommandSchemaHash" not in svc_src or "schemaHash = BridgeCommandSchemaHash" not in cmd_src:
        errors.append("bridge_cmd: BridgeCommandSchemaHash must be computed and exposed in ping/capabilities")

    # Canonical tabNames parity (kills hardcoded `4`)
    expected_tabs = list(packaging.get("tabNames", []))
    max_tab = len(expected_tabs) - 1
    m_tabs = re.search(r"CanonicalTabNames\s*=\s*\[([^\]]+)\]", svc_src)
    if not m_tabs:
        errors.append("bridge_cmd: missing CanonicalTabNames in LiveDiagnosticBridgeService.cs")
    else:
        actual_tabs = re.findall(r'"([^"]+)"', m_tabs.group(1))
        if actual_tabs != expected_tabs:
            errors.append(f"bridge_cmd: CanonicalTabNames {actual_tabs} != federation.json {expected_tabs}")

    if re.search(r'GetArgInt\(\s*argsEl\s*,\s*"(?:index|tab)"\s*,\s*-1\s*,\s*-1\s*,\s*4\s*\)', cmd_src):
        errors.append("bridge_cmd: hardcoded tab upper bound `4` still present in LiveDiagnosticBridgeService.Commands.cs (use MaxTabIndex)")
    if re.search(r"target\s*<\s*0\s*\|\|\s*target\s*>\s*4\b", main_src):
        errors.append("bridge_cmd: hardcoded tab upper bound `4` still present in MainActivity.LiveBridge.cs (use MaxTabIndex)")

    # Verify tab_switch & open_file arg schema ranges match max_tab
    expected_tab_range = f"0..{max_tab}"
    expected_tab_union = "|".join(f"'{t}'" for t in expected_tabs)
    for _ename, _num, cmd_name, _tier, args_schema in catalog:
        if cmd_name == "tab_switch":
            if expected_tab_range not in args_schema or expected_tab_union not in args_schema:
                errors.append(f"bridge_cmd: tab_switch args schema drifted from federation tabNames ({expected_tab_range}, {expected_tab_union})")
        elif cmd_name == "open_file":
            if expected_tab_range not in args_schema:
                errors.append(f"bridge_cmd: open_file args schema drifted from federation tabNames ({expected_tab_range})")

    meta = {
        "opcodes": len(opcodes),
        "catalog": len(catalog),
        "switchCases": len(switch_cases),
        "maxTabIndex": max_tab,
        "schemaHash": schema_hash,
    }
    return errors, meta


# ==================== 2. PACKAGING UNIFICATION ====================


def check_packaging(owner_repo: str, packaging: dict[str, Any]) -> tuple[list[str], dict[str, Any]]:
    errors: list[str] = []
    app_id = str(packaging["applicationId"])
    ver_prefix = str(packaging["displayVersionPrefix"])
    min_sdk = int(packaging["minSdk"])
    target_sdk = int(packaging["targetSdk"])
    apk_name = str(packaging["apkAssetName"])
    ua_name = str(packaging["userAgentName"])
    ua_ver = str(packaging["userAgentVersion"])
    req_libs = sorted(str(x) for x in packaging["requiredNativeLibs"])

    # 1. CavernizeAndroid.csproj
    csproj = _read_text(CSPROJ_PATH)
    m_appid = re.search(r"<ApplicationId>([^<]+)</ApplicationId>", csproj)
    m_disp = re.search(r"<ApplicationDisplayVersion>([^<]+)</ApplicationDisplayVersion>", csproj)
    m_minsdk = re.search(r"<SupportedOSPlatformVersion>([^<]+)</SupportedOSPlatformVersion>", csproj)
    if not m_appid or m_appid.group(1).strip() != app_id:
        errors.append(f"packaging: csproj ApplicationId '{m_appid.group(1) if m_appid else None}' != '{app_id}'")
    if not m_disp or not m_disp.group(1).strip().startswith(f"{ver_prefix}."):
        errors.append(f"packaging: csproj ApplicationDisplayVersion '{m_disp.group(1) if m_disp else None}' does not start with '{ver_prefix}.'")
    if not m_minsdk or m_minsdk.group(1).strip() != f"{min_sdk}.0":
        errors.append(f"packaging: csproj SupportedOSPlatformVersion '{m_minsdk.group(1) if m_minsdk else None}' != '{min_sdk}.0'")

    # 2. AndroidManifest.xml
    manifest = _read_text(MANIFEST_PATH)
    m_pkg = re.search(r'package="([^"]+)"', manifest)
    m_m_min = re.search(r'android:minSdkVersion="(\d+)"', manifest)
    m_m_tgt = re.search(r'android:targetSdkVersion="(\d+)"', manifest)
    if not m_pkg or m_pkg.group(1) != app_id:
        errors.append(f"packaging: AndroidManifest package '{m_pkg.group(1) if m_pkg else None}' != '{app_id}'")
    if not m_m_min or int(m_m_min.group(1)) != min_sdk:
        errors.append(f"packaging: AndroidManifest minSdkVersion != {min_sdk}")
    if not m_m_tgt or int(m_m_tgt.group(1)) != target_sdk:
        errors.append(f"packaging: AndroidManifest targetSdkVersion != {target_sdk}")

    # 3. BuildAndroid.yml
    yml = _read_text(BUILD_YML_PATH)
    if f"{ver_prefix}.${{{{ github.run_number }}}}" not in yml:
        errors.append(f"packaging: BuildAndroid.yml missing version prefix '{ver_prefix}.${{{{ github.run_number }}}}'")
    if owner_repo not in yml:
        errors.append(f"packaging: BuildAndroid.yml missing ownerRepo '{owner_repo}'")
    for lib in req_libs:
        if f'"{lib}"' not in yml:
            errors.append(f"packaging: BuildAndroid.yml missing required native lib '{lib}'")

    # 4. AppUpdateService.cs
    upd = _read_text(UPDATE_SVC_PATH)
    const_checks = {
        "RepoOwnerAndName": owner_repo,
        "DisplayVersionPrefix": ver_prefix,
        "FallbackDisplayVersion": f"{ver_prefix}.0",
        "UserAgentProductName": ua_name,
        "UserAgentProductVersion": ua_ver,
        "TargetAssetName": apk_name,
    }
    for cname, expected_val in const_checks.items():
        mc = re.search(rf'\b{cname}\s*=\s*"([^"]+)"', upd)
        if not mc or mc.group(1) != expected_val:
            errors.append(f"packaging: AppUpdateService.{cname} '{mc.group(1) if mc else None}' != '{expected_val}'")
    m_libs = re.search(r"RequiredNativeLibs\s*=\s*\[([^\]]+)\]", upd, re.S)
    if not m_libs:
        errors.append("packaging: AppUpdateService.RequiredNativeLibs missing")
    else:
        actual_libs = sorted(re.findall(r'"([^"]+)"', m_libs.group(1)))
        if actual_libs != req_libs:
            errors.append(f"packaging: AppUpdateService.RequiredNativeLibs {actual_libs} != {req_libs}")

    # 5. LiveDiagnosticBridgeService.Commands.cs reuses AppUpdateService.RequiredNativeLibs
    cmd_src = _read_text(BRIDGE_CMD_PATH)
    if "AppUpdateService.RequiredNativeLibs" not in cmd_src:
        errors.append("packaging: LiveDiagnosticBridgeService.Commands.cs must reuse AppUpdateService.RequiredNativeLibs")

    return errors, {
        "ownerRepo": owner_repo,
        "applicationId": app_id,
        "displayVersionPrefix": ver_prefix,
        "minSdk": min_sdk,
        "targetSdk": target_sdk,
        "requiredNativeLibs": req_libs,
    }


# ==================== 3. APP SETTINGS SCHEMA & ZERO-REFLECTION RESET ====================


@dataclass(frozen=True)
class SettingProp:
    name: str
    cs_type: str
    kind: str
    pref_key: str
    default_expr: str
    default_display: str
    is_secret: bool
    is_writable: bool


def _is_secret_name(name: str) -> bool:
    low = name.lower()
    return any(tok in low for tok in SECRET_TOKENS)


def _cs_type_to_kind(cs_type: str, is_writable: bool) -> str:
    if not is_writable:
        return "ReadOnly"
    return {
        "bool": "Bool",
        "int": "Int",
        "long": "Long",
        "float": "Float",
        "double": "Double",
        "string": "String",
        "HashSet<string>": "StringSet",
    }.get(cs_type, "ReadOnly")


def parse_app_settings_store() -> tuple[list[SettingProp], list[str]]:
    src = _read_text(SETTINGS_STORE_PATH)
    errors: list[str] = []

    if "public sealed partial class AppSettingsStore" not in src:
        errors.append("settings_schema: AppSettingsStore must be declared `public sealed partial class AppSettingsStore`")

    # Resolve private string constants like MvsepPendingJobsPrefKey = "mvsepPendingJobsV1"
    const_map: dict[str, str] = {}
    for cm in re.finditer(r'\bconst\s+string\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*"([^"]*)"', src):
        const_map[cm.group(1)] = cm.group(2)

    # Check put-only pref key drift (accounting for ReadStringSetPref & prefs.Contains)
    get_keys = set(re.findall(r'prefs\.Get(?:Boolean|Int|Long|Float|String)\(\s*"([^"]+)"', src))
    get_keys.update(re.findall(r'ReadStringSetPref\(\s*"([^"]+)"', src))
    get_keys.update(re.findall(r'prefs\.Contains\(\s*"([^"]+)"', src))
    put_keys = set(re.findall(r'\.Put(?:Boolean|Int|Long|Float|String)\(\s*"([^"]+)"', src))
    put_only = sorted(put_keys - get_keys)
    if put_only:
        errors.append(f"settings_schema: put-only drifted pref keys in AppSettingsStore.cs: {put_only}")

    props: list[SettingProp] = []
    prop_re = re.compile(r"(?m)^    public\s+([A-Za-z0-9_<>\[\]\.\?]+)\s+([A-Za-z0-9_]+)\s*(\{|=>)")
    for m in prop_re.finditer(src):
        cs_type, prop_name, opener = m.group(1), m.group(2), m.group(3)
        is_secret = _is_secret_name(prop_name)
        if opener == "=>":
            props.append(
                SettingProp(
                    name=prop_name,
                    cs_type=cs_type,
                    kind="ReadOnly",
                    pref_key="",
                    default_expr="",
                    default_display="(read-only)",
                    is_secret=is_secret,
                    is_writable=False,
                )
            )
            continue

        body, _ = _extract_balanced(src, m.end(), "{", "}")
        has_set = bool(re.search(r"\bset\b", body))
        is_noop_set = bool(re.search(r"\bset\s*\{\s*\}", body))
        writable = has_set and not is_noop_set

        pref_key = ""
        default_expr = ""
        default_display = ""

        # Case A: prefs.Get*(key, default)
        m_get = re.search(r"prefs\.Get(?:Boolean|Int|Long|Float|String)\s*\(", body)
        if m_get:
            args_raw, _ = _extract_balanced(body, m_get.end(), "(", ")")
            parts = _split_top_level_args(args_raw)
            if len(parts) >= 2:
                raw_key = parts[0].strip()
                if raw_key.startswith('"') and raw_key.endswith('"'):
                    pref_key = raw_key[1:-1]
                elif raw_key in const_map:
                    pref_key = const_map[raw_key]
                else:
                    pref_key = raw_key
                default_expr = parts[1].strip()
                default_display = default_expr
        else:
            # Case B: ReadStringSetPref("key", ...)
            m_ss = re.search(r'ReadStringSetPref\(\s*"([^"]+)"', body)
            if m_ss:
                pref_key = m_ss.group(1)
                default_expr = "new HashSet<string>(StringComparer.OrdinalIgnoreCase)"
                default_display = "[]"
            else:
                # Case C: constant getter (e.g., get => true; set { })
                m_const = re.search(r"get\s*=>\s*([^;]+);", body)
                if m_const:
                    default_expr = m_const.group(1).strip()
                    default_display = default_expr

        if writable and not default_expr:
            errors.append(f"settings_schema: could not extract default expression for writable property '{prop_name}'")

        props.append(
            SettingProp(
                name=prop_name,
                cs_type=cs_type,
                kind=_cs_type_to_kind(cs_type, writable),
                pref_key=pref_key,
                default_expr=default_expr,
                default_display=default_display,
                is_secret=is_secret,
                is_writable=writable,
            )
        )

    # Sort canonically by property name (LC_ALL=C ASCII ordinal order)
    props.sort(key=lambda p: p.name)
    return props, errors


def render_settings_schema_cs(props: list[SettingProp]) -> tuple[str, str]:
    canon_lines = [
        f"{p.name}|{p.kind}|{p.pref_key}|{p.default_display}|w={int(p.is_writable)}|s={int(p.is_secret)}\n"
        for p in props
    ]
    schema_hash = hashlib.sha256("".join(canon_lines).encode("utf-8")).hexdigest()

    lines: list[str] = [
        "// <auto-generated by .agents/scripts/co_evolve.py --sync (W56.4.3). Do NOT hand-edit.>",
        "using Cavern;",
        "using Cavern.CavernSettings;",
        "using Cavernize.Logic.Models.RenderTargets;",
        "",
        "namespace CavernizeAndroid.Services;",
        "",
        "/// <summary>Typed value kind for a setting declared in <see cref=\"AppSettingsStore\"/>.</summary>",
        "public enum SettingValueKind {",
        "    Bool = 0,",
        "    Int = 1,",
        "    Long = 2,",
        "    Float = 3,",
        "    Double = 4,",
        "    String = 5,",
        "    StringSet = 6,",
        "    ReadOnly = 7",
        "}",
        "",
        "/// <summary>Zero-reflection metadata &amp; typed reset descriptor for an <see cref=\"AppSettingsStore\"/> property.</summary>",
        "public sealed class SettingDescriptor {",
        "    public string Name { get; }",
        "    public string PrefKey { get; }",
        "    public SettingValueKind Kind { get; }",
        "    public string DefaultDisplay { get; }",
        "    public bool IsSecret { get; }",
        "    public bool IsWritable { get; }",
        "    internal Action<AppSettingsStore> ResetAction { get; }",
        "",
        "    public SettingDescriptor(",
        "        string name,",
        "        string prefKey,",
        "        SettingValueKind kind,",
        "        string defaultDisplay,",
        "        bool isSecret,",
        "        bool isWritable,",
        "        Action<AppSettingsStore> resetAction) {",
        "        Name = name;",
        "        PrefKey = prefKey;",
        "        Kind = kind;",
        "        DefaultDisplay = defaultDisplay;",
        "        IsSecret = isSecret;",
        "        IsWritable = isWritable;",
        "        ResetAction = resetAction;",
        "    }",
        "}",
        "",
        "public sealed partial class AppSettingsStore {",
        f'    /// <summary>Deterministic SHA-256 hash over all {len(props)} canonical setting descriptors.</summary>',
        f'    public const string SettingsSchemaHash = "{schema_hash}";',
        "",
        "    /// <summary>Canonical typed descriptors for all public <see cref=\"AppSettingsStore\"/> properties.</summary>",
        "    public static readonly SettingDescriptor[] SchemaDescriptors = [",
    ]

    for idx, p in enumerate(props):
        comma = "," if idx + 1 < len(props) else ""
        sec = "true" if p.is_secret else "false"
        wri = "true" if p.is_writable else "false"
        if p.is_writable and not p.is_secret and p.default_expr:
            action = f"static s => s.{p.name} = {p.default_expr}"
        else:
            action = "null"
        disp = _escape_csharp_string(p.default_display)
        pkey = _escape_csharp_string(p.pref_key)
        lines.append(
            f'        new("{p.name}", "{pkey}", SettingValueKind.{p.kind}, "{disp}", {sec}, {wri}, {action}){comma}'
        )

    lines.extend(
        [
            "    ];",
            "",
            "    static readonly Dictionary<string, SettingDescriptor> SchemaByName =",
            "        SchemaDescriptors.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);",
            "",
            "    /// <summary>",
            "    /// Resets a single non-secret writable setting back to its true declared default via its compiled",
            "    /// typed property setter (zero reflection, zero <c>Activator.CreateInstance</c>, preserving setter side-effects).",
            "    /// </summary>",
            "    public bool TryResetSetting(string key, out string canonicalName, out string defaultDisplay, out string error) {",
            '        canonicalName = "";',
            '        defaultDisplay = "";',
            '        error = "";',
            "        if (string.IsNullOrWhiteSpace(key)) {",
            '            error = "Setting key is required.";',
            "            return false;",
            "        }",
            "        if (!SchemaByName.TryGetValue(key.Trim(), out SettingDescriptor desc)) {",
            '            error = $"Writable setting \'{key}\' not found.";',
            "            return false;",
            "        }",
            "        if (desc.IsSecret) {",
            '            error = $"Rejected: \'{desc.Name}\' is a protected secret property.";',
            "            return false;",
            "        }",
            "        if (!desc.IsWritable || desc.ResetAction == null) {",
            '            error = $"Setting \'{desc.Name}\' is read-only or invariant and cannot be reset.";',
            "            return false;",
            "        }",
            "        desc.ResetAction(this);",
            "        ApplyGlobals();",
            "        canonicalName = desc.Name;",
            "        defaultDisplay = desc.DefaultDisplay;",
            "        return true;",
            "    }",
            "}",
            "",
        ]
    )
    return "\n".join(lines), schema_hash


def check_or_sync_settings_schema(sync: bool) -> tuple[list[str], dict[str, Any]]:
    props, errors = parse_app_settings_store()
    cmd_src = _read_text(BRIDGE_CMD_PATH)
    if "Activator.CreateInstance" in cmd_src:
        errors.append("settings_schema: LiveDiagnosticBridgeService.Commands.cs still uses Activator.CreateInstance")
    if "store.TryResetSetting(" not in cmd_src:
        errors.append("settings_schema: settings_reset must call store.TryResetSetting(...)")

    generated_cs, schema_hash = render_settings_schema_cs(props)
    synced = False
    if sync:
        current = _read_text(SETTINGS_SCHEMA_PATH) if SETTINGS_SCHEMA_PATH.is_file() else None
        if current != generated_cs:
            SETTINGS_SCHEMA_PATH.write_bytes(generated_cs.encode("utf-8"))
            synced = True
    else:
        if not SETTINGS_SCHEMA_PATH.is_file():
            errors.append("settings_schema: AppSettingsStore.Schema.cs missing (run `co_evolve.py --sync`)")
        else:
            current = _read_text(SETTINGS_SCHEMA_PATH)
            if current != generated_cs:
                errors.append("settings_schema: AppSettingsStore.Schema.cs drifted from AppSettingsStore.cs (run `co_evolve.py --sync`)")

    writable_cnt = sum(1 for p in props if p.is_writable and not p.is_secret)
    secret_cnt = sum(1 for p in props if p.is_secret)
    return errors, {
        "totalProperties": len(props),
        "resettableProperties": writable_cnt,
        "protectedSecrets": secret_cnt,
        "schemaHash": schema_hash,
        "synced": synced,
    }


# ==================== 4. RPE <-> SBE DSP PARITY & MEMORY GOVERNOR ====================


def check_rpe_sbe_and_memory_governor() -> tuple[list[str], dict[str, Any]]:
    errors: list[str] = []

    rpe_files = sorted(RPE_DIR.glob("RealtimePlayerEngine*.cs"))
    rpe_src = "\n".join(_read_text(p) for p in rpe_files)
    sbe_src = "\n".join(_read_text(f) for f in sorted(RPE_DIR.glob("StudioBatchExporter*.cs")))

    for stage in DSP_PARITY_LEDGER["shared_stages"]:
        sid = stage["id"]
        for tok in stage["rpe_tokens"]:
            if tok not in rpe_src:
                errors.append(f"rpe_sbe_dsp_parity: shared stage '{sid}' missing RPE token '{tok}'")
        for tok in stage["sbe_tokens"]:
            if tok not in sbe_src:
                errors.append(f"rpe_sbe_dsp_parity: shared stage '{sid}' missing SBE token '{tok}'")

    for exc in DSP_PARITY_LEDGER["rt_only_exceptions"]:
        sid = exc["id"]
        for tok in exc["rpe_tokens"]:
            if tok not in rpe_src:
                errors.append(f"rpe_sbe_dsp_parity: RT-only exception '{sid}' missing RPE token '{tok}'")

    for exc in DSP_PARITY_LEDGER["export_only_exceptions"]:
        sid = exc["id"]
        for tok in exc["sbe_tokens"]:
            if tok not in sbe_src:
                errors.append(f"rpe_sbe_dsp_parity: Export-only exception '{sid}' missing SBE token '{tok}'")

    # MemoryBudgetGovernor invariants (W56.4.2)
    nm_src = _read_text(NEURAL_MODELS_PATH)
    ort_src = _read_text(ONNX_BRIDGE_PATH)
    if "class MemoryBudgetGovernor" not in nm_src or "struct DeviceMemorySnapshot" not in nm_src:
        errors.append("memory_governor: MemoryBudgetGovernor / DeviceMemorySnapshot missing in NeuralAudioModels.cs")
    if "HardSingleProcessPssCeilingBytes = 2600L * 1024L * 1024L" not in nm_src:
        errors.append("memory_governor: HardSingleProcessPssCeilingBytes must remain 2600 MB in NeuralAudioModels.cs")
    if "HysteresisBandBytes = 150L * 1024L * 1024L" not in nm_src:
        errors.append("memory_governor: HysteresisBandBytes must remain 150 MB in NeuralAudioModels.cs")

    # Verify zero filename heuristics inside IsMemoryHeavyModel methods
    m_nm_heavy = re.search(r"public\s+static\s+bool\s+IsMemoryHeavyModel\([^)]*\)\s*(?:=>[^;]+;|\{[^}]*\})", nm_src, re.S)
    if not m_nm_heavy or "roformer" in m_nm_heavy.group(0).lower() or "demucs" in m_nm_heavy.group(0).lower():
        errors.append("memory_governor: NeuralModelCatalog.IsMemoryHeavyModel must delegate to MemoryBudgetGovernor without filename string heuristics")
    m_ort_heavy = re.search(r"internal\s+static\s+bool\s+IsMemoryHeavyModel\([^)]*\)\s*(?:=>[^;]+;|\{[^}]*\})", ort_src, re.S)
    if not m_ort_heavy or "roformer" in m_ort_heavy.group(0).lower() or "demucs" in m_ort_heavy.group(0).lower():
        errors.append("memory_governor: NativeOnnxRuntimeBridge.IsMemoryHeavyModel must delegate to MemoryBudgetGovernor without filename string heuristics")

    # Verify ZERO /proc/pressure reads across CavernizeAndroid (SELinux API 35 EACCES)
    for cs_path in sorted(ANDROID_ROOT.rglob("*.cs")):
        if "obj" in cs_path.parts or "bin" in cs_path.parts:
            continue
        txt = _read_text(cs_path)
        if "/proc/pressure" in txt:
            rel = cs_path.relative_to(REPO_ROOT).as_posix()
            errors.append(f"memory_governor: forbidden '/proc/pressure' read found in {rel}")

    return errors, {
        "sharedStages": len(DSP_PARITY_LEDGER["shared_stages"]),
        "rtOnlyExceptions": len(DSP_PARITY_LEDGER["rt_only_exceptions"]),
        "exportOnlyExceptions": len(DSP_PARITY_LEDGER["export_only_exceptions"]),
    }


def run_all(sync: bool) -> dict[str, Any]:
    os.environ["LC_ALL"] = "C"
    t0 = time.perf_counter()
    owner_repo, packaging = _load_federation_packaging()

    e_bridge, m_bridge = check_bridge_cmd(packaging)
    e_pkg, m_pkg = check_packaging(owner_repo, packaging)
    e_set, m_set = check_or_sync_settings_schema(sync=sync)
    e_dsp, m_dsp = check_rpe_sbe_and_memory_governor()

    all_errors = e_bridge + e_pkg + e_set + e_dsp
    elapsed_ms = round((time.perf_counter() - t0) * 1000.0, 2)
    return {
        "ok": len(all_errors) == 0,
        "mode": "sync" if sync else "check",
        "elapsedMs": elapsed_ms,
        "errors": all_errors,
        "domains": {
            "bridge_cmd": m_bridge,
            "packaging": m_pkg,
            "settings_schema": m_set,
            "rpe_sbe_dsp_parity": m_dsp,
        },
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Cavern Zero-Static Co-Evolution & Parity Engine")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--check", action="store_true", help="Verify zero drift across all domains (default)")
    group.add_argument("--sync", action="store_true", help="Regenerate derived C# schema artifacts deterministically and verify")
    parser.add_argument("--json", action="store_true", help="Emit sorted deterministic JSON summary")
    args = parser.parse_args(argv)

    result = run_all(sync=bool(args.sync))
    if args.json:
        print(json.dumps(result, sort_keys=True, indent=2))
    else:
        status = "OK" if result["ok"] else f"FAIL({len(result['errors'])})"
        b = result["domains"]["bridge_cmd"]
        s = result["domains"]["settings_schema"]
        d = result["domains"]["rpe_sbe_dsp_parity"]
        print(
            f"CO_EVOLVE|{status}|bridge={b.get('opcodes', 0)}/{b.get('switchCases', 0)}"
            f"|settings={s.get('totalProperties', 0)}(reset={s.get('resettableProperties', 0)})"
            f"|dsp={d.get('sharedStages', 0)}+{d.get('rtOnlyExceptions', 0)}+{d.get('exportOnlyExceptions', 0)}"
            f"|ms={result['elapsedMs']}"
        )
        for err in result["errors"]:
            print(f"  - {err}", file=sys.stderr)
    return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
