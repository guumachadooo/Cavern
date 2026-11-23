#!/usr/bin/env python3
"""GitHub-Parity Local Multi-Layer Compiler, Roslyn Android Type-Checker & Packaging Auditor.

Mirrors `.github/workflows/BuildAndroid.yml` locally on BOTH:
  - Linux x86_64 / ARM64 containers (e.g. AI Studio, Docker, CI, desktop Linux): auto-detects or
    auto-bootstraps official portable `.NET` SDK matching `CavernizeAndroid.csproj` TFM into
    `~/.cache/cavern-android-ref/dotnet` (zero workspace pollution, zero root required).
  - Termux ARM64 PRoot (`/root/.dotnet/dotnet`): preserves low-RAM sequential flags and Roslyn toolset.

Checks performed (with `--ci-parity` / default full mode):
  00_Native_C11       : Host C11 `-Wall -Wextra -Werror` compilation & execution of `test_proaudio_native.c`
  01_Cavern           : `Cavern/Cavern.csproj` (`netstandard2.1` strict C# 8.0 + XML docs `CS1591`)
  02_Cavern.Format    : `Cavern.Format/Cavern.Format.csproj` (`netstandard2.1` strict C# 8.0 + XML docs)
  03_Cavernize.Logic  : `CavernSamples/Reusable/Cavernize.Logic/Cavernize.Logic.csproj`
  04_CavernizeAndroid : Ephemeral shadow Roslyn compilation in `/tmp/cavern-android-typecheck/` against
                        dynamically resolved `Microsoft.Android.Ref.<API>` + `.NET` Platform Compatibility
                        Analyzers (`CA1416`, `CA1422`, `CS0618`, `CS1570`, `CS0419`, `CS0649`, all `CS*` errors)
  05_Packaging_L10n   : `AndroidManifest.xml` W^X/targetSdk, `CavernizeAndroid.csproj` `XA4301` check,
                        `BuildAndroid.yml` OTA version prefix sync, and 100% `AndroidStrings.cs` parity/syntax.
"""

from __future__ import annotations

import argparse
import collections
import io
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import time
import urllib.request
import xml.etree.ElementTree as ET
import xml.sax.saxutils as saxutils
import zipfile
from pathlib import Path

REQUIRED_ANDROID_REFS = (
    "Mono.Android.dll",
    "Java.Interop.dll",
    "Mono.Android.Export.dll",
    "Mono.Android.Runtime.dll",
)

FALLBACK_ANDROID_API = 37
FALLBACK_ANDROID_VER = "37.2.31"
FALLBACK_NETCORE_VER = "10.0.15"
FALLBACK_ANALYZER_VER = "8.0.31"
ROSLYN_PKG = "microsoft.net.compilers.toolset"
ROSLYN_VER = "4.12.0"

DIAG_ERR_RE = re.compile(r"(?::\s*error\b|\berror\s+[A-Z]{2,}[0-9]+:|\bfatal error:)", re.I)
DIAG_WARN_RE = re.compile(r"(?::\s*warning\b|\bwarning\s+[A-Z]{2,}[0-9]+:)", re.I)
DIAG_CODE_RE = re.compile(r"\b(?:warning|error)\s+([A-Z]{2,6}[0-9]{3,5})\b", re.I)
PROJ_SUFFIX_RE = re.compile(r"\s*\[[^\]]+\.csproj\]\s*$", re.I)


def _find_repo_root() -> Path:
    here = Path(__file__).resolve()
    for cand in (here.parents[2], Path.cwd()):
        if (cand / "CavernSamples" / "CavernizeAndroid" / "CavernizeAndroid.csproj").is_file():
            return cand
    raise SystemExit("ERROR: Could not locate Cavern repository root.")


def _read_android_csproj_meta(repo: Path) -> tuple[int, str, list[str]]:
    """Read target .NET major version, SupportedOSPlatformVersion, and NoWarn from CavernizeAndroid.csproj."""
    csproj = repo / "CavernSamples" / "CavernizeAndroid" / "CavernizeAndroid.csproj"
    txt = csproj.read_text(encoding="utf-8", errors="ignore")
    tfm_m = re.search(r"<TargetFramework>\s*net(\d+)\.\d+-android", txt, re.I)
    net_major = int(tfm_m.group(1)) if tfm_m else 11
    min_m = re.search(r"<SupportedOSPlatformVersion>\s*([0-9.]+)\s*</SupportedOSPlatformVersion>", txt, re.I)
    min_sdk = min_m.group(1) if min_m else "26.0"
    nowarns: list[str] = []
    for m in re.finditer(r"<NoWarn>([^<]+)</NoWarn>", txt, re.I):
        for tok in re.split(r"[;,\s]+", m.group(1)):
            tok = tok.strip()
            if tok and not tok.startswith("$("):
                nowarns.append(tok)
    return net_major, min_sdk, sorted(set(nowarns))


def _is_proot_dotnet(dotnet_bin: str) -> bool:
    return dotnet_bin == "/root/.dotnet/dotnet" and platform.machine().lower() in ("aarch64", "arm64")


def _build_dotnet_env(dotnet_bin: str) -> dict[str, str]:
    env = os.environ.copy()
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_EnableDiagnostics"] = "0"
    if _is_proot_dotnet(dotnet_bin):
        env["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1"
        env["DOTNET_GCHeapHardLimit"] = "20000000"
    else:
        # Container sandboxes (e.g. gVisor) work reliably with W^X JIT disabled
        env.setdefault("DOTNET_EnableWriteXorExecute", "0")
    return env


def _probe_dotnet_version(dotnet_bin: str) -> tuple[int, str] | None:
    try:
        res = subprocess.run(
            [dotnet_bin, "--version"],
            env=_build_dotnet_env(dotnet_bin),
            capture_output=True,
            text=True,
            timeout=15,
            check=False,
        )
        if res.returncode == 0 and res.stdout.strip():
            ver = res.stdout.strip().splitlines()[-1].strip()
            major = int(ver.split(".", 1)[0])
            return major, ver
    except Exception:
        pass
    return None


def _bootstrap_portable_dotnet(cache_dir: Path, target_major: int) -> str | None:
    """Install official portable Microsoft .NET SDK into cache_dir/dotnet (zero workspace pollution)."""
    sdk_dir = cache_dir / f"dotnet-{target_major}"
    dotnet_exe = sdk_dir / "dotnet"
    if dotnet_exe.is_file() and _probe_dotnet_version(str(dotnet_exe)):
        return str(dotnet_exe)

    if shutil.which("bash") is None:
        return None

    sdk_dir.mkdir(parents=True, exist_ok=True)
    installer = cache_dir / "dotnet-install.sh"
    try:
        if not installer.is_file() or installer.stat().st_size < 1000:
            print(f"Bootstrapping portable .NET {target_major}.0 SDK -> {sdk_dir}...", flush=True)
            req = urllib.request.Request(
                "https://dot.net/v1/dotnet-install.sh",
                headers={"User-Agent": "cavern-ci-parity/2.0"},
            )
            with urllib.request.urlopen(req, timeout=45) as resp:
                installer.write_bytes(resp.read())

        attempts = [
            ["bash", str(installer), "--channel", f"{target_major}.0", "--install-dir", str(sdk_dir), "--no-path"],
            ["bash", str(installer), "--channel", f"{target_major}.0", "--quality", "preview", "--install-dir", str(sdk_dir), "--no-path"],
            ["bash", str(installer), "--channel", "STS", "--install-dir", str(sdk_dir), "--no-path"],
        ]
        for cmd in attempts:
            res = subprocess.run(cmd, capture_output=True, text=True, timeout=180, check=False)
            if res.returncode == 0 and dotnet_exe.is_file() and _probe_dotnet_version(str(dotnet_exe)):
                return str(dotnet_exe)
    except Exception as exc:
        print(f"Note: portable .NET bootstrap failed ({exc}).", file=sys.stderr)
    return None


def _resolve_dotnet(cache_dir: Path, target_major: int, allow_bootstrap: bool) -> tuple[str | None, int, str]:
    candidates: list[str] = []
    env_bin = os.environ.get("CAVERN_DOTNET_BIN", "").strip()
    if env_bin:
        candidates.append(env_bin)
    which_bin = shutil.which("dotnet")
    if which_bin:
        candidates.append(which_bin)
    candidates.extend([
        str(cache_dir / f"dotnet-{target_major}" / "dotnet"),
        "/tmp/cavern-dotnet-test/sdk/dotnet",
        "/root/.dotnet/dotnet",
        str(Path.home() / ".dotnet" / "dotnet"),
    ])

    best: tuple[str, int, str] | None = None
    seen: set[str] = set()
    for cand in candidates:
        if not cand or cand in seen or not Path(cand).is_file():
            continue
        seen.add(cand)
        info = _probe_dotnet_version(cand)
        if info:
            major, ver = info
            if major >= target_major:
                return cand, major, ver
            if best is None or major > best[1]:
                best = (cand, major, ver)

    if best is not None:
        return best

    if allow_bootstrap:
        boot = _bootstrap_portable_dotnet(cache_dir, target_major)
        if boot:
            info = _probe_dotnet_version(boot)
            if info:
                return boot, info[0], info[1]

    return None, 0, ""


def _nuget_latest_version(pkg_id: str, prefer_stable: bool = True, major_prefix: str = "") -> str | None:
    url = f"https://api.nuget.org/v3-flatcontainer/{pkg_id.lower()}/index.json"
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "cavern-ci-parity/2.0"})
        with urllib.request.urlopen(req, timeout=20) as resp:
            versions = json.loads(resp.read().decode("utf-8")).get("versions", [])
        if major_prefix:
            versions = [v for v in versions if v.startswith(major_prefix)]
        if not versions:
            return None
        if prefer_stable:
            stable = [v for v in versions if "-" not in v]
            if stable:
                return stable[-1]
        return versions[-1]
    except Exception:
        return None


def _resolve_android_ref_target(cache_dir: Path, net_major: int) -> tuple[int, str, str]:
    """Resolve matching Microsoft.Android.Ref.<API> package dynamically (cached metadata)."""
    # Android API level tracks .NET major closely (net11->API 37, net10->API 36, net9->API 35)
    preferred_apis = [net_major + 26, net_major + 25, 37, 36, 35]
    seen_apis: list[int] = []
    for a in preferred_apis:
        if a not in seen_apis and a >= 34:
            seen_apis.append(a)

    # Check local cache first to avoid unnecessary NuGet index lookups
    meta_file = cache_dir / f"android-ref-meta-net{net_major}.json"
    if meta_file.is_file():
        try:
            meta = json.loads(meta_file.read_text(encoding="utf-8"))
            api = int(meta["api"])
            pkg = str(meta["pkg"])
            ver = str(meta["ver"])
            tdir = cache_dir / f"api{api}-{ver}"
            if all((tdir / name).is_file() for name in REQUIRED_ANDROID_REFS):
                return api, pkg, ver
        except Exception:
            pass

    for api in seen_apis:
        pkg = f"microsoft.android.ref.{api}"
        ver = _nuget_latest_version(pkg, prefer_stable=True)
        if ver:
            cache_dir.mkdir(parents=True, exist_ok=True)
            meta_file.write_text(json.dumps({"api": api, "pkg": pkg, "ver": ver}), encoding="utf-8")
            return api, pkg, ver

    return FALLBACK_ANDROID_API, f"microsoft.android.ref.{FALLBACK_ANDROID_API}", FALLBACK_ANDROID_VER


def _ensure_android_refs(cache_dir: Path, api: int, pkg: str, ver: str) -> list[Path]:
    target_dir = cache_dir / f"api{api}-{ver}"
    paths = [target_dir / name for name in REQUIRED_ANDROID_REFS]
    if all(p.is_file() and p.stat().st_size > 0 for p in paths):
        return paths

    target_dir.mkdir(parents=True, exist_ok=True)
    url = f"https://api.nuget.org/v3-flatcontainer/{pkg}/{ver}/{pkg}.{ver}.nupkg"
    print(f"Downloading {pkg} {ver} reference assemblies -> {target_dir}...", flush=True)
    req = urllib.request.Request(url, headers={"User-Agent": "cavern-ci-parity/2.0"})
    with urllib.request.urlopen(req, timeout=90) as resp:
        nupkg_bytes = resp.read()

    with zipfile.ZipFile(io.BytesIO(nupkg_bytes)) as zf:
        names = zf.namelist()
        for name in REQUIRED_ANDROID_REFS:
            pat = re.compile(rf"^ref/net[^/]+/{re.escape(name)}$")
            matched = next((n for n in names if pat.match(n)), None)
            if not matched:
                raise RuntimeError(f"Expected `{name}` inside `{pkg}.{ver}.nupkg`.")
            (target_dir / name).write_bytes(zf.read(matched))

    return paths


def _ensure_netcore_refs(cache_dir: Path, target_major: int) -> list[Path]:
    """Cache managed Microsoft.NETCore.App.Ref DLLs when running shadow check on an older host SDK (e.g. PRoot .NET 8)."""
    ver = _nuget_latest_version("microsoft.netcore.app.ref", prefer_stable=True, major_prefix=f"{target_major}.") or FALLBACK_NETCORE_VER
    target_dir = cache_dir / f"netcore-{ver}"
    marker = target_dir / ".ready"
    if marker.is_file() and (target_dir / "System.Runtime.dll").is_file():
        return sorted(target_dir.glob("*.dll"))

    target_dir.mkdir(parents=True, exist_ok=True)
    url = f"https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.ref/{ver}/microsoft.netcore.app.ref.{ver}.nupkg"
    print(f"Downloading microsoft.netcore.app.ref {ver} -> {target_dir}...", flush=True)
    req = urllib.request.Request(url, headers={"User-Agent": "cavern-ci-parity/2.0"})
    with urllib.request.urlopen(req, timeout=90) as resp:
        nupkg_bytes = resp.read()

    pat = re.compile(r"^ref/net[^/]+/([^/]+\.dll)$")
    with zipfile.ZipFile(io.BytesIO(nupkg_bytes)) as zf:
        for member in zf.namelist():
            m = pat.match(member)
            if m:
                (target_dir / m.group(1)).write_bytes(zf.read(member))

    dlls = sorted(target_dir.glob("*.dll"))
    if not (target_dir / "System.Runtime.dll").is_file():
        raise RuntimeError(f"Failed extracting ref DLLs from microsoft.netcore.app.ref.{ver}.nupkg")
    marker.write_text("ok\n", encoding="utf-8")
    return dlls


def _ensure_netcore_analyzers(cache_dir: Path, dotnet_bin: str) -> list[Path]:
    dotnet_root = Path(dotnet_bin).resolve().parent
    packs_dir = dotnet_root / "packs" / "Microsoft.NETCore.App.Ref"
    if packs_dir.is_dir():
        candidates = sorted(
            (p for p in packs_dir.iterdir() if (p / "analyzers" / "dotnet" / "cs").is_dir()),
            key=lambda p: [int(x) if x.isdigit() else 0 for x in re.split(r"[.-]", p.name)],
        )
        if candidates:
            local_dlls = sorted((candidates[-1] / "analyzers" / "dotnet" / "cs").glob("*.dll"))
            if local_dlls:
                return local_dlls

    target_dir = cache_dir / f"analyzers-{FALLBACK_ANALYZER_VER}"
    marker = target_dir / ".ready"
    if marker.is_file():
        return sorted(target_dir.glob("*.dll"))

    target_dir.mkdir(parents=True, exist_ok=True)
    url = (
        f"https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.ref/"
        f"{FALLBACK_ANALYZER_VER}/microsoft.netcore.app.ref.{FALLBACK_ANALYZER_VER}.nupkg"
    )
    req = urllib.request.Request(url, headers={"User-Agent": "cavern-ci-parity/2.0"})
    with urllib.request.urlopen(req, timeout=90) as resp:
        nupkg_bytes = resp.read()
    pat = re.compile(r"^analyzers/dotnet/cs/([^/]+\.dll)$")
    with zipfile.ZipFile(io.BytesIO(nupkg_bytes)) as zf:
        for member in zf.namelist():
            m = pat.match(member)
            if m:
                (target_dir / m.group(1)).write_bytes(zf.read(member))
    marker.write_text("ok\n", encoding="utf-8")
    return sorted(target_dir.glob("*.dll"))


def _ensure_roslyn_toolset(cache_dir: Path) -> Path | None:
    target_dir = cache_dir / f"roslyn-{ROSLYN_VER}"
    props_path = target_dir / "build" / "Microsoft.Net.Compilers.Toolset.props"
    csc_dll = target_dir / "tasks" / "netcore" / "bincore" / "csc.dll"
    if props_path.is_file() and csc_dll.is_file():
        return props_path
    try:
        target_dir.mkdir(parents=True, exist_ok=True)
        url = f"https://api.nuget.org/v3-flatcontainer/{ROSLYN_PKG}/{ROSLYN_VER}/{ROSLYN_PKG}.{ROSLYN_VER}.nupkg"
        req = urllib.request.Request(url, headers={"User-Agent": "cavern-ci-parity/2.0"})
        with urllib.request.urlopen(req, timeout=90) as resp:
            nupkg_bytes = resp.read()
        with zipfile.ZipFile(io.BytesIO(nupkg_bytes)) as zf:
            for member in zf.namelist():
                if (member.startswith("build/") or member.startswith("tasks/netcore/")) and not member.endswith("/"):
                    dest = target_dir / member
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    dest.write_bytes(zf.read(member))
        return props_path if props_path.is_file() and csc_dll.is_file() else None
    except Exception:
        return None


def _generate_resource_stub(
    android_dir: Path,
    stub_path: Path,
    android_api: int,
    min_sdk: str,
    emit_platform_attrs: bool,
    root_namespace: str = "CavernizeAndroid",
) -> None:
    res_dir = android_dir / "Resources"
    groups: dict[str, set[str]] = {
        "Drawable": set(), "Layout": set(), "Mipmap": set(),
        "Raw": set(), "Xml": set(), "Id": set(), "String": set(),
    }
    if res_dir.is_dir():
        for entry in res_dir.rglob("*"):
            if not entry.is_file():
                continue
            folder = entry.parent.name.split("-", 1)[0].lower()
            cls_name = {
                "drawable": "Drawable", "layout": "Layout", "mipmap": "Mipmap",
                "raw": "Raw", "xml": "Xml", "values": None,
            }.get(folder)
            if cls_name:
                ident = re.sub(r"[^A-Za-z0-9_]", "_", entry.stem)
                if ident and ident[0].isdigit():
                    ident = "_" + ident
                if ident:
                    groups.setdefault(cls_name, set()).add(ident)

    lines = [
        "// <auto-generated by check_android_cs.py for Roslyn type-check />",
    ]
    if emit_platform_attrs:
        lines.append(f'[assembly: System.Runtime.Versioning.TargetPlatform("android{android_api}.0")]')
        lines.append(f'[assembly: System.Runtime.Versioning.SupportedOSPlatform("android{min_sdk}")]')
    lines.extend([
        f"namespace {root_namespace} {{",
        "    public static partial class Resource {",
    ])
    for cls_name in sorted(groups):
        lines.append(f"        public static partial class {cls_name} {{")
        for ident in sorted(groups[cls_name]):
            lines.append(f"            public const int {ident} = 0;")
        lines.append("        }")
    lines.extend(["    }", "}", ""])
    stub_path.write_text("\n".join(lines), encoding="utf-8")


def _write_shadow_project(
    repo: Path,
    shadow_dir: Path,
    sdk_major: int,
    target_major: int,
    android_api: int,
    min_sdk: str,
    csproj_nowarns: list[str],
    android_ref_dlls: list[Path],
    extra_netcore_ref_dlls: list[Path],
    analyzer_dlls: list[Path],
    roslyn_props: Path | None,
    android_subdir: str = "CavernSamples/CavernizeAndroid",
    root_namespace: str = "CavernizeAndroid",
    include_cavern_refs: bool = True,
) -> tuple[Path, int]:
    android_dir = repo / android_subdir
    shadow_dir.mkdir(parents=True, exist_ok=True)
    native_modern_sdk = sdk_major >= target_major
    stub_path = shadow_dir / "Resource.Stub.cs"
    _generate_resource_stub(android_dir, stub_path, android_api, min_sdk, emit_platform_attrs=native_modern_sdk, root_namespace=root_namespace)

    cs_files: list[Path] = []
    for p in sorted(android_dir.rglob("*.cs")):
        rel_parts = set(p.relative_to(android_dir).parts)
        if "bin" in rel_parts or "obj" in rel_parts:
            continue
        cs_files.append(p)

    # When host SDK major >= target major (e.g. .NET 11 SDK), use SDK's native net11.0 framework refs + built-in
    # Roslyn platform analyzers (enabling CA1416/CA1422/CS1570/CS0419 parity with GitHub Actions!).
    shadow_tfm = f"net{target_major}.0" if native_modern_sdk else f"net{sdk_major}.0"

    props_lines = [
        "<Project>",
        "  <PropertyGroup>",
        f"    <BaseIntermediateOutputPath>{saxutils.escape(str(shadow_dir / 'obj'))}/</BaseIntermediateOutputPath>",
        f"    <BaseOutputPath>{saxutils.escape(str(shadow_dir / 'bin'))}/</BaseOutputPath>",
        "  </PropertyGroup>",
    ]
    if roslyn_props is not None and roslyn_props.is_file() and not native_modern_sdk:
        props_lines.append(f'  <Import Project="{saxutils.escape(str(roslyn_props))}" />')
    props_lines.append("</Project>")
    (shadow_dir / "Directory.Build.props").write_text("\n".join(props_lines) + "\n", encoding="utf-8")

    compile_items = [f'    <Compile Include="{saxutils.escape(str(p))}" />' for p in cs_files]
    compile_items.append(f'    <Compile Include="{saxutils.escape(str(stub_path))}" />')

    all_refs = list(extra_netcore_ref_dlls) + list(android_ref_dlls)
    ref_items = [
        f'    <Reference Include="{saxutils.escape(dll.stem)}">\n'
        f'      <HintPath>{saxutils.escape(str(dll))}</HintPath>\n'
        f'      <Private>false</Private>\n'
        f'    </Reference>'
        for dll in all_refs
    ]
    analyzer_items = [
        f'    <Analyzer Include="{saxutils.escape(str(dll))}" />'
        for dll in analyzer_dlls
    ]

    proj_refs = [
        repo / "Cavern" / "Cavern.csproj",
        repo / "Cavern.Format" / "Cavern.Format.csproj",
        repo / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Cavernize.Logic.csproj",
    ] if include_cavern_refs else []
    proj_ref_items = [f'    <ProjectReference Include="{saxutils.escape(str(pr))}" />' for pr in proj_refs]

    # Match CavernizeAndroid.csproj warnings: enable CA1416/CA1422 platform analysis on modern SDK!
    nowarn_list = list(csproj_nowarns)
    if not native_modern_sdk:
        nowarn_list.extend(["CA1416", "CA1422"])
    # Ignore CS1591 on the app project (CavernizeAndroid does not require XML docs on every UI type,
    # while GenerateDocumentationFile enables CS1570 / CS0419 / CS1573 XML doc checks like CI!)
    nowarn_list.append("CS1591")
    nowarn_str = ";".join(sorted(set(nowarn_list)))

    extra_props: list[str] = []
    if native_modern_sdk:
        extra_props.extend([
            "    <EnableNETAnalyzers>true</EnableNETAnalyzers>",
            "    <AnalysisLevel>latest</AnalysisLevel>",
            "    <GenerateDocumentationFile>true</GenerateDocumentationFile>",
            f"    <DocumentationFile>{saxutils.escape(str(shadow_dir / 'bin' / 'CavernizeAndroid.TypeCheck.xml'))}</DocumentationFile>",
        ])
    else:
        extra_props.extend([
            "    <LangVersion>preview</LangVersion>",
            "    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>",
            "    <DisableTransitiveFrameworkReferences>true</DisableTransitiveFrameworkReferences>",
            "    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>",
        ])

    csproj_xml = "\n".join([
        '<Project Sdk="Microsoft.NET.Sdk">',
        "  <PropertyGroup>",
        f"    <TargetFramework>{shadow_tfm}</TargetFramework>",
        "    <OutputType>Library</OutputType>",
        "    <Nullable>disable</Nullable>",
        "    <ImplicitUsings>enable</ImplicitUsings>",
        "    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>",
        "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>",
        "    <GenerateDependencyFile>false</GenerateDependencyFile>",
        "    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>",
        f"    <RootNamespace>{root_namespace}</RootNamespace>",
        f"    <AssemblyName>{root_namespace}.TypeCheck</AssemblyName>",
        f"    <DefineConstants>$(DefineConstants);ANDROID;__ANDROID__;ANDROID_{android_api}_0_OR_GREATER</DefineConstants>",
        f"    <NoWarn>$(NoWarn);{nowarn_str}</NoWarn>",
        *extra_props,
        "  </PropertyGroup>",
        "  <ItemGroup>",
        *compile_items,
        "  </ItemGroup>",
        *(["  <ItemGroup>", *proj_ref_items, "  </ItemGroup>"] if proj_ref_items else []),
        "  <ItemGroup>",
        *ref_items,
        "  </ItemGroup>",
        *(["  <ItemGroup>", *analyzer_items, "  </ItemGroup>"] if analyzer_items else []),
        "</Project>",
        "",
    ])
    shadow_csproj = shadow_dir / f"{root_namespace}.TypeCheck.csproj"
    prev_xml = shadow_csproj.read_text(encoding="utf-8") if shadow_csproj.is_file() else None
    if prev_xml != csproj_xml:
        shutil.rmtree(shadow_dir / "obj", ignore_errors=True)
        shadow_csproj.write_text(csproj_xml, encoding="utf-8")
    return shadow_csproj, len(cs_files)


def _extract_diags(output: str, repo: Path) -> tuple[list[str], list[str]]:
    errs: list[str] = []
    warns: list[str] = []
    seen_e: set[str] = set()
    seen_w: set[str] = set()
    repo_prefix = str(repo.resolve()).rstrip("/") + "/"

    for raw in output.splitlines():
        line = raw.strip()
        if not line or re.match(r"^\d+\s+(?:Warning|Error)\(s\)$", line, re.I):
            continue
        if "NETSDK1057" in line:
            continue
        short = line.replace(repo_prefix, "")
        key = PROJ_SUFFIX_RE.sub("", short).replace("\\", "/")
        if DIAG_ERR_RE.search(short):
            if key not in seen_e:
                seen_e.add(key)
                errs.append(short)
        elif DIAG_WARN_RE.search(short):
            if key not in seen_w:
                seen_w.add(key)
                warns.append(short)
    return errs, warns


def _run_layer_build(
    dotnet_bin: str,
    target_csproj: Path,
    cwd: Path,
    env: dict[str, str],
    extra_args: list[str] | None = None,
) -> tuple[int, str]:
    cmd = [
        dotnet_bin,
        "build",
        str(target_csproj),
        "-c",
        "Release",
        "-m:1",
        "/p:UseSharedCompilation=false",
        "-p:GenerateFullPaths=true",
        "--nologo",
        "-v:minimal",
    ]
    if extra_args:
        cmd.extend(extra_args)
    res = subprocess.run(cmd, cwd=str(cwd), env=env, capture_output=True, text=True, timeout=240, check=False)
    combined = (res.stdout + "\n" + res.stderr).strip()
    return res.returncode, combined


def _run_native_c11_test(repo: Path) -> tuple[str, list[str], list[str]]:
    if shutil.which("gcc") is None:
        return "SKIP", [], []
    nat_dir = repo / "CavernSamples" / "CavernizeAndroid" / "Native" / "cavernize_proaudio"
    c1 = nat_dir / "cavernize_uac2_core.c"
    c2 = nat_dir / "test_proaudio_native.c"
    if not (c1.is_file() and c2.is_file()):
        return "SKIP", [], []
    out_bin = Path("/tmp/cavern_test_proaudio_native")
    cmd = ["gcc", "-std=c11", "-O2", "-Wall", "-Wextra", "-Werror", f"-I{nat_dir}", str(c1), str(c2), "-lm", "-o", str(out_bin)]
    res = subprocess.run(cmd, cwd=str(repo), capture_output=True, text=True, timeout=60, check=False)
    if res.returncode != 0:
        return "FAIL", [f"Native C11 compile failed: {(res.stderr or res.stdout).strip()}"], []
    run_res = subprocess.run([str(out_bin)], capture_output=True, text=True, timeout=30, check=False)
    out_bin.unlink(missing_ok=True)
    if run_res.returncode != 0:
        return "FAIL", [f"Native C11 unit test failed: {(run_res.stdout + run_res.stderr).strip()}"], []
    return "PASS", [], []


def _run_static_packaging_and_l10n_audit(repo: Path) -> tuple[str, list[str], list[str]]:
    errs: list[str] = []
    warns: list[str] = []

    # 1. AndroidManifest.xml W^X & targetSdkVersion guard
    manifest = repo / "CavernSamples" / "CavernizeAndroid" / "Properties" / "AndroidManifest.xml"
    if manifest.is_file():
        mtxt = manifest.read_text(encoding="utf-8", errors="ignore")
        try:
            ET.fromstring(mtxt)
        except ET.ParseError as exc:
            errs.append(f"AndroidManifest.xml: invalid XML ({exc})")
        if not re.search(r'android:targetSdkVersion\s*=\s*"(?:35|36)"', mtxt):
            errs.append("AndroidManifest.xml: missing android:targetSdkVersion=\"35\" (W^X policy)")
        if 'android:extractNativeLibs="true"' not in mtxt:
            errs.append("AndroidManifest.xml: missing android:extractNativeLibs=\"true\" (W^X policy)")
        if "requestLegacyExternalStorage" in mtxt:
            errs.append("AndroidManifest.xml: forbidden requestLegacyExternalStorage present")

    # 2. CavernizeAndroid.csproj XML & XA4301 duplicate native-lib guard
    csproj = repo / "CavernSamples" / "CavernizeAndroid" / "CavernizeAndroid.csproj"
    if csproj.is_file():
        ctxt = csproj.read_text(encoding="utf-8", errors="ignore")
        try:
            ET.fromstring(ctxt)
        except ET.ParseError as exc:
            errs.append(f"CavernizeAndroid.csproj: invalid XML ({exc})")
        for m_nat in re.finditer(r"<AndroidNativeLibrary\b([^>]*)>", ctxt, re.S):
            attrs = m_nat.group(1)
            if re.search(r'\bInclude="[^"]*\.so"', attrs) and "EnableDefaultAndroidItems" not in attrs:
                warns.append("CavernizeAndroid.csproj: unconditional <AndroidNativeLibrary Include=...*.so> may trigger XA4301")

    # 3. OTA version prefix sync (CavernizeAndroid.csproj vs BuildAndroid.yml)
    wf = repo / ".github" / "workflows" / "BuildAndroid.yml"
    if csproj.is_file() and wf.is_file():
        m1 = re.search(r"<ApplicationDisplayVersion>([^<]+)</ApplicationDisplayVersion>", csproj.read_text(encoding="utf-8", errors="ignore"))
        m2 = re.search(r"-p:ApplicationDisplayVersion=([^\n\\]+)", wf.read_text(encoding="utf-8", errors="ignore"))
        if m1 and m2:
            p1 = ".".join(m1.group(1).strip().split(".")[:2])
            p2 = ".".join(m2.group(1).strip().split(".")[:2])
            if p1 != p2:
                errs.append(f"OTA version prefix mismatch: csproj={m1.group(1).strip()} vs BuildAndroid.yml={m2.group(1).strip()}")

    # 4. AndroidStrings*.cs 100% EN/PT-BR parity + format placeholders + statement terminator check
    strings_cs = repo / "CavernSamples" / "CavernizeAndroid" / "Language" / "AndroidStrings.cs"
    strings_pt_cs = repo / "CavernSamples" / "CavernizeAndroid" / "Language" / "AndroidStrings.PtBr.cs"
    if strings_cs.is_file():
        stxt = strings_cs.read_text(encoding="utf-8", errors="ignore")
        pt_src_path = strings_pt_cs if strings_pt_cs.is_file() else strings_cs
        pt_txt = pt_src_path.read_text(encoding="utf-8", errors="ignore")
        m_en = re.search(r"static\s+Dictionary<string,\s*string>\s+CreateEnglish\(\)\s*=>\s*new\(\)\s*\{(.*?)(?=^\s*\};)", stxt, re.M | re.S)
        m_pt = re.search(r"static\s+void\s+ApplyPortuguese\b(.*?)(?=^\s*static\s+void\s+Apply|\Z)", pt_txt, re.M | re.S)
        if m_en and m_pt:
            en_keys = re.findall(r'^\s*\["([^"]+)"\]\s*=', m_en.group(1), re.M)
            pt_keys = re.findall(r'^\s*d\["([^"]+)"\]\s*=', m_pt.group(1), re.M)
            en_set, pt_set = set(en_keys), set(pt_keys)
            missing_pt = sorted(en_set - pt_set)
            if missing_pt:
                errs.append(f"{pt_src_path.name}: {len(missing_pt)} EN key(s) missing in ApplyPortuguese: {', '.join(missing_pt[:8])}")
            en_dups = [k for k, c in collections.Counter(en_keys).items() if c > 1]
            if en_dups:
                errs.append(f"AndroidStrings.cs: duplicate EN dictionary key(s): {', '.join(en_dups[:8])}")
            # Catch dangling trailing comma in ApplyPortuguese statement body (CI Run #45 regression!)
            pt_start_line = pt_txt[: m_pt.start()].count("\n") + 1
            for idx, ln in enumerate(m_pt.group(0).splitlines(), start=pt_start_line):
                if re.match(r'^\s*d\["[^"]+"\]\s*=\s*".*",\s*(?://.*)?$', ln):
                    errs.append(f"{pt_src_path.name}:{idx}: trailing comma `,` instead of `;` in ApplyPortuguese statement (CS1002/CS1513)")

    status = "FAIL" if errs else ("WARN" if warns else "PASS")
    return status, errs, warns


def main() -> int:
    parser = argparse.ArgumentParser(description="GitHub-Parity Local Multi-Layer Compiler & Android Roslyn Type-Checker.")
    parser.add_argument("--ci-parity", action="store_true", help="Run all 6 CI-parity layers (Native C11, Cavern, Format, Logic, Android Roslyn+Analyzers, Packaging/L10n).")
    parser.add_argument("--android-only", action="store_true", help="Run only the CavernizeAndroid shadow Roslyn check (fast mode).")
    parser.add_argument("--no-bootstrap", action="store_true", help="Do not auto-download portable .NET SDK if dotnet is missing.")
    parser.add_argument("--strict-warnings", action="store_true", help="Exit non-zero if any compiler/analyzer warning is emitted.")
    parser.add_argument("--keep-bin", action="store_true", help="Keep Cavern*/{bin,obj} folders after build.")
    args = parser.parse_args()

    full_mode = not args.android_only
    t0 = time.time()
    repo = _find_repo_root()
    cache_dir = Path.home() / ".cache" / "cavern-android-ref"
    shadow_dir = Path("/tmp/cavern-android-typecheck")

    target_major, min_sdk, csproj_nowarns = _read_android_csproj_meta(repo)
    dotnet_bin, sdk_major, sdk_ver = _resolve_dotnet(cache_dir, target_major, allow_bootstrap=not args.no_bootstrap)
    if not dotnet_bin:
        print("ERROR: No .NET SDK found and portable bootstrap unavailable.", file=sys.stderr)
        return 1

    env = _build_dotnet_env(dotnet_bin)
    layer_results: list[tuple[str, str, list[str], list[str]]] = []

    if full_mode:
        st0, e0, w0 = _run_native_c11_test(repo)
        layer_results.append(("00_Native_C11", st0, e0, w0))

        for lbl, rel_proj in (
            ("01_Cavern", "Cavern/Cavern.csproj"),
            ("02_Cavern.Format", "Cavern.Format/Cavern.Format.csproj"),
            ("03_Cavernize.Logic", "CavernSamples/Reusable/Cavernize.Logic/Cavernize.Logic.csproj"),
        ):
            rc_l, out_l = _run_layer_build(dotnet_bin, repo / rel_proj, repo, env)
            el, wl = _extract_diags(out_l, repo)
            if rc_l != 0 and not el:
                el.append(f"{lbl} exited with code {rc_l}: {out_l[-400:]}")
            layer_results.append((lbl, "PASS" if rc_l == 0 else "FAIL", el, wl))

    # Layer 04: CavernizeAndroid shadow Roslyn compilation with real Microsoft.Android.Ref + Analyzers
    try:
        android_api, android_pkg, android_ver = _resolve_android_ref_target(cache_dir, target_major)
        android_refs = _ensure_android_refs(cache_dir, android_api, android_pkg, android_ver)
        native_modern_sdk = sdk_major >= target_major
        extra_netcore_refs: list[Path] = [] if native_modern_sdk else _ensure_netcore_refs(cache_dir, target_major)
        analyzers = [] if native_modern_sdk else _ensure_netcore_analyzers(cache_dir, dotnet_bin)
        roslyn_props = None if native_modern_sdk else _ensure_roslyn_toolset(cache_dir)
    except Exception as exc:
        print(f"ANDROID REF FETCH ERROR: {exc}", file=sys.stderr)
        return 1

    shadow_csproj, cs_count = _write_shadow_project(
        repo, shadow_dir, sdk_major, target_major, android_api, min_sdk, csproj_nowarns,
        android_refs, extra_netcore_refs, analyzers, roslyn_props,
    )

    mode_tag = f"sdk-{sdk_ver}/api{android_api}({android_ver})/analyzers={'ON' if native_modern_sdk else 'compat'}"
    print(f"Checking {cs_count} CavernizeAndroid C# files ({mode_tag})...", flush=True)
    rc_a, out_a = _run_layer_build(dotnet_bin, shadow_csproj, shadow_dir, env)
    if rc_a != 0 and roslyn_props is not None and ": error CS" not in out_a:
        shadow_csproj, cs_count = _write_shadow_project(
            repo, shadow_dir, sdk_major, target_major, android_api, min_sdk, csproj_nowarns,
            android_refs, extra_netcore_refs, analyzers, None,
        )
        rc_a, out_a = _run_layer_build(dotnet_bin, shadow_csproj, shadow_dir, env)

    ea, wa = _extract_diags(out_a, repo)
    if rc_a != 0 and not ea:
        ea.append(f"04_CavernizeAndroid exited with code {rc_a}: {out_a[-400:]}")
    layer_results.append(("04_CavernizeAndroid", "PASS" if rc_a == 0 else "FAIL", ea, wa))

    if full_mode:
        st5, e5, w5 = _run_static_packaging_and_l10n_audit(repo)
        layer_results.append(("05_Packaging_L10n", st5, e5, w5))

        # Layer 06: Optional standalone OmniAgentAndroid companion Roslyn + Analyzer verification
        omni_csproj_path = repo / "CavernSamples" / "OmniAgentAndroid" / "OmniAgentAndroid.csproj"
        if omni_csproj_path.is_file():
            omni_shadow_dir = Path("/tmp/omniagent-android-typecheck")
            omni_shadow_csproj, omni_cs_count = _write_shadow_project(
                repo, omni_shadow_dir, sdk_major, target_major, android_api, min_sdk, csproj_nowarns,
                android_refs, extra_netcore_refs, analyzers, roslyn_props,
                android_subdir="CavernSamples/OmniAgentAndroid",
                root_namespace="OmniAgentAndroid",
                include_cavern_refs=False,
            )
            print(f"Checking {omni_cs_count} OmniAgentAndroid C# files ({mode_tag})...", flush=True)
            rc_o, out_o = _run_layer_build(dotnet_bin, omni_shadow_csproj, omni_shadow_dir, env)
            eo, wo = _extract_diags(out_o, repo)
            if rc_o != 0 and not eo:
                eo.append(f"06_OmniAgentAndroid exited with code {rc_o}: {out_o[-400:]}")
            layer_results.append(("06_OmniAgentAndroid", "PASS" if rc_o == 0 else "FAIL", eo, wo))

    # Clean transient bin/obj created by core project builds so repo stays clean
    if not args.keep_bin and not _is_proot_dotnet(dotnet_bin):
        for rel_d in ("Cavern", "Cavern.Format", "CavernSamples/Reusable/Cavernize.Logic"):
            for sub in ("bin", "obj"):
                shutil.rmtree(repo / rel_d / sub, ignore_errors=True)

    # Consolidated GitHub-parity report
    all_errs: list[tuple[str, str]] = []
    all_warns: list[tuple[str, str]] = []
    seen_global_e: set[str] = set()
    seen_global_w: set[str] = set()
    warn_codes: collections.Counter[str] = collections.Counter()
    err_codes: collections.Counter[str] = collections.Counter()

    print("\n================ LOCAL CI-PARITY BUILD REPORT ================")
    for lbl, st, el, wl in layer_results:
        print(f"- {lbl:20s}: {st:4s} (errors={len(el)}, warnings={len(wl)})")
        for line in el:
            k = PROJ_SUFFIX_RE.sub("", line)
            if k not in seen_global_e:
                seen_global_e.add(k)
                all_errs.append((lbl, line))
                m = DIAG_CODE_RE.search(line)
                err_codes[m.group(1).upper() if m else "GENERAL"] += 1
        for line in wl:
            k = PROJ_SUFFIX_RE.sub("", line)
            if k not in seen_global_w:
                seen_global_w.add(k)
                all_warns.append((lbl, line))
                m = DIAG_CODE_RE.search(line)
                warn_codes[m.group(1).upper() if m else "GENERAL"] += 1

    if all_errs:
        print(f"\nERRORS ({len(all_errs)} unique):")
        for lbl, line in all_errs:
            print(f"  [{lbl}] {line}")
    if all_warns:
        print(f"\nWARNINGS ({len(all_warns)} unique):")
        for lbl, line in all_warns:
            print(f"  [{lbl}] {line}")
        print("  Code breakdown:", ", ".join(f"{k}={v}" for k, v in warn_codes.most_common()))

    elapsed = time.time() - t0
    print("==============================================================")
    print(
        f"[check_android_cs|sdk={sdk_ver}|api={android_api}|files={cs_count}|"
        f"errors={len(all_errs)}|warnings={len(all_warns)}|elapsed={elapsed:.2f}s]"
    )
    if all_errs:
        return 1
    if args.strict_warnings and all_warns:
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
