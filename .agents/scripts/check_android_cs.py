#!/usr/bin/env python3
"""Local Roslyn C# type-check for CavernizeAndroid on Termux ARM64 PRoot.

Caches architecture-independent managed Android reference assemblies
(`Microsoft.Android.Ref.36` / API 36) + `.NET 10.0` ref assemblies (`Microsoft.NETCore.App.Ref`)
and Roslyn 4.12 (`Microsoft.Net.Compilers.Toolset 4.12.0`, targeting .NET 8 with C# 13 support)
under `~/.cache/cavern-android-ref/`, and compiles all `CavernSamples/CavernizeAndroid/**/*.cs`
via an ephemeral shadow `.csproj` in `/tmp/cavern-android-typecheck/` (zero workspace pollution).
"""

from __future__ import annotations

import io
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request
import xml.sax.saxutils as saxutils
import zipfile
from pathlib import Path

ANDROID_REF_PKG = "microsoft.android.ref.36"
ANDROID_REF_VER = "36.1.118"
ANDROID_REF_URL = (
    f"https://api.nuget.org/v3-flatcontainer/{ANDROID_REF_PKG}/{ANDROID_REF_VER}/"
    f"{ANDROID_REF_PKG}.{ANDROID_REF_VER}.nupkg"
)
REQUIRED_REFS = (
    "Mono.Android.dll",
    "Java.Interop.dll",
    "Mono.Android.Export.dll",
    "Mono.Android.Runtime.dll",
)

NETCORE_REF_PKG = "microsoft.netcore.app.ref"
NETCORE_REF_VER = "10.0.15"
NETCORE_REF_URL = (
    f"https://api.nuget.org/v3-flatcontainer/{NETCORE_REF_PKG}/{NETCORE_REF_VER}/"
    f"{NETCORE_REF_PKG}.{NETCORE_REF_VER}.nupkg"
)
NETCORE_ANALYZER_VER = "8.0.31"
NETCORE_ANALYZER_URL = (
    f"https://api.nuget.org/v3-flatcontainer/{NETCORE_REF_PKG}/{NETCORE_ANALYZER_VER}/"
    f"{NETCORE_REF_PKG}.{NETCORE_ANALYZER_VER}.nupkg"
)

ROSLYN_PKG = "microsoft.net.compilers.toolset"
ROSLYN_VER = "4.12.0"
ROSLYN_URL = (
    f"https://api.nuget.org/v3-flatcontainer/{ROSLYN_PKG}/{ROSLYN_VER}/"
    f"{ROSLYN_PKG}.{ROSLYN_VER}.nupkg"
)


def _find_repo_root() -> Path:
    here = Path(__file__).resolve()
    for cand in (here.parents[2], Path.cwd()):
        if (cand / "CavernSamples" / "CavernizeAndroid" / "CavernizeAndroid.csproj").is_file():
            return cand
    raise SystemExit("ERROR: Could not locate Cavern repository root.")


def _ensure_android_refs(cache_dir: Path) -> list[Path]:
    target_dir = cache_dir / ANDROID_REF_VER
    paths = [target_dir / name for name in REQUIRED_REFS]
    if all(p.is_file() and p.stat().st_size > 0 for p in paths):
        return paths

    target_dir.mkdir(parents=True, exist_ok=True)
    print(f"Downloading {ANDROID_REF_PKG} {ANDROID_REF_VER} reference assemblies -> {target_dir}...", flush=True)
    req = urllib.request.Request(ANDROID_REF_URL, headers={"User-Agent": "cavern-typecheck/1.0"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        nupkg_bytes = resp.read()

    with zipfile.ZipFile(io.BytesIO(nupkg_bytes)) as zf:
        names = zf.namelist()
        for name in REQUIRED_REFS:
            member = f"ref/net10.0/{name}"
            if member not in names:
                pat = re.compile(rf"^ref/net[^/]+/{re.escape(name)}$")
                matched = next((n for n in names if pat.match(n)), None)
                if not matched:
                    raise RuntimeError(f"Expected `{member}` inside `{ANDROID_REF_PKG}.{ANDROID_REF_VER}.nupkg`.")
                member = matched
            (target_dir / name).write_bytes(zf.read(member))

    return paths


def _ensure_netcore_refs(cache_dir: Path) -> list[Path]:
    """Cache managed .NET 10 reference assemblies matching Microsoft.Android.Ref.36."""
    target_dir = cache_dir / f"netcore-{NETCORE_REF_VER}"
    marker = target_dir / ".ready"
    if marker.is_file() and (target_dir / "System.Runtime.dll").is_file():
        dlls = sorted(target_dir.glob("*.dll"))
        if dlls:
            return dlls

    target_dir.mkdir(parents=True, exist_ok=True)
    print(f"Downloading {NETCORE_REF_PKG} {NETCORE_REF_VER} reference assemblies -> {target_dir}...", flush=True)
    req = urllib.request.Request(NETCORE_REF_URL, headers={"User-Agent": "cavern-typecheck/1.0"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        nupkg_bytes = resp.read()

    pat = re.compile(r"^ref/net[^/]+/([^/]+\.dll)$")
    with zipfile.ZipFile(io.BytesIO(nupkg_bytes)) as zf:
        for member in zf.namelist():
            m = pat.match(member)
            if not m:
                continue
            (target_dir / m.group(1)).write_bytes(zf.read(member))

    dlls = sorted(target_dir.glob("*.dll"))
    if not (target_dir / "System.Runtime.dll").is_file() or not dlls:
        raise RuntimeError(f"Failed to extract ref DLLs from `{NETCORE_REF_PKG}.{NETCORE_REF_VER}.nupkg`.")
    marker.write_text("ok\n", encoding="utf-8")
    return dlls


def _ensure_netcore_analyzers(cache_dir: Path, dotnet_bin: str) -> list[Path]:
    """Return Roslyn 4.5-compatible .NET source generators (e.g. GeneratedRegex) for Roslyn 4.12."""
    dotnet_root = Path(dotnet_bin).resolve().parent if "/" in dotnet_bin else Path("/root/.dotnet")
    packs_dir = dotnet_root / "packs" / "Microsoft.NETCore.App.Ref"
    if packs_dir.is_dir():
        candidates = sorted(
            (p for p in packs_dir.glob("8.0.*") if (p / "analyzers" / "dotnet" / "cs").is_dir()),
            key=lambda p: [int(x) if x.isdigit() else 0 for x in p.name.split(".")],
        )
        if candidates:
            local_dlls = sorted((candidates[-1] / "analyzers" / "dotnet" / "cs").glob("*.dll"))
            if local_dlls:
                return local_dlls

    target_dir = cache_dir / f"analyzers-{NETCORE_ANALYZER_VER}"
    marker = target_dir / ".ready"
    if marker.is_file():
        dlls = sorted(target_dir.glob("*.dll"))
        if dlls:
            return dlls

    target_dir.mkdir(parents=True, exist_ok=True)
    print(f"Downloading {NETCORE_REF_PKG} {NETCORE_ANALYZER_VER} analyzers -> {target_dir}...", flush=True)
    req = urllib.request.Request(NETCORE_ANALYZER_URL, headers={"User-Agent": "cavern-typecheck/1.0"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        nupkg_bytes = resp.read()

    pat = re.compile(r"^analyzers/dotnet/cs/([^/]+\.dll)$")
    with zipfile.ZipFile(io.BytesIO(nupkg_bytes)) as zf:
        for member in zf.namelist():
            m = pat.match(member)
            if not m:
                continue
            (target_dir / m.group(1)).write_bytes(zf.read(member))

    dlls = sorted(target_dir.glob("*.dll"))
    if not dlls:
        raise RuntimeError(f"Failed to extract analyzers from `{NETCORE_REF_PKG}.{NETCORE_ANALYZER_VER}.nupkg`.")
    marker.write_text("ok\n", encoding="utf-8")
    return dlls


def _ensure_roslyn_toolset(cache_dir: Path) -> Path | None:
    """Download & cache managed Roslyn 4.12.0 (C# 13 on .NET 8) if reachable."""
    target_dir = cache_dir / f"roslyn-{ROSLYN_VER}"
    props_path = target_dir / "build" / "Microsoft.Net.Compilers.Toolset.props"
    csc_dll = target_dir / "tasks" / "netcore" / "bincore" / "csc.dll"
    if props_path.is_file() and csc_dll.is_file():
        return props_path

    try:
        target_dir.mkdir(parents=True, exist_ok=True)
        print(f"Downloading {ROSLYN_PKG} {ROSLYN_VER} (C# 13 Roslyn) -> {target_dir}...", flush=True)
        req = urllib.request.Request(ROSLYN_URL, headers={"User-Agent": "cavern-typecheck/1.0"})
        with urllib.request.urlopen(req, timeout=60) as resp:
            nupkg_bytes = resp.read()

        with zipfile.ZipFile(io.BytesIO(nupkg_bytes)) as zf:
            for member in zf.namelist():
                if not (member.startswith("build/") or member.startswith("tasks/netcore/")):
                    continue
                if member.endswith("/"):
                    continue
                dest = target_dir / member
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes(zf.read(member))
        return props_path if props_path.is_file() and csc_dll.is_file() else None
    except Exception as exc:
        print(f"Note: could not load Roslyn {ROSLYN_VER} ({exc}); falling back to SDK Roslyn.", flush=True)
        return None


def _generate_resource_stub(android_dir: Path, stub_path: Path) -> None:
    res_dir = android_dir / "Resources"
    groups: dict[str, set[str]] = {
        "Drawable": set(),
        "Layout": set(),
        "Mipmap": set(),
        "Raw": set(),
        "Xml": set(),
        "Id": set(),
        "String": set(),
    }
    if res_dir.is_dir():
        for entry in res_dir.rglob("*"):
            if not entry.is_file():
                continue
            folder = entry.parent.name.split("-", 1)[0].lower()
            cls_name = {
                "drawable": "Drawable",
                "layout": "Layout",
                "mipmap": "Mipmap",
                "raw": "Raw",
                "xml": "Xml",
                "values": None,
            }.get(folder)
            if cls_name:
                ident = re.sub(r"[^A-Za-z0-9_]", "_", entry.stem)
                if ident and ident[0].isdigit():
                    ident = "_" + ident
                if ident:
                    groups.setdefault(cls_name, set()).add(ident)

    lines = [
        "// <auto-generated by check_android_cs.py for Roslyn type-check />",
        "namespace CavernizeAndroid {",
        "    public static partial class Resource {",
    ]
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
    ref_dlls: list[Path],
    analyzer_dlls: list[Path],
    roslyn_props: Path | None,
) -> tuple[Path, int]:
    android_dir = repo / "CavernSamples" / "CavernizeAndroid"
    shadow_dir.mkdir(parents=True, exist_ok=True)
    stub_path = shadow_dir / "Resource.Stub.cs"
    _generate_resource_stub(android_dir, stub_path)

    cs_files: list[Path] = []
    for p in sorted(android_dir.rglob("*.cs")):
        rel_parts = set(p.relative_to(android_dir).parts)
        if "bin" in rel_parts or "obj" in rel_parts:
            continue
        cs_files.append(p)

    # Put Directory.Build.props in shadow_dir so BaseIntermediateOutputPath stays in /tmp
    # and optional Roslyn 4.12 toolset props import at NuGet props timing.
    props_lines = [
        "<Project>",
        "  <PropertyGroup>",
        f"    <BaseIntermediateOutputPath>{saxutils.escape(str(shadow_dir / 'obj'))}/</BaseIntermediateOutputPath>",
        f"    <BaseOutputPath>{saxutils.escape(str(shadow_dir / 'bin'))}/</BaseOutputPath>",
        "  </PropertyGroup>",
    ]
    if roslyn_props is not None and roslyn_props.is_file():
        props_lines.append(f'  <Import Project="{saxutils.escape(str(roslyn_props))}" />')
    props_lines.append("</Project>")
    (shadow_dir / "Directory.Build.props").write_text("\n".join(props_lines) + "\n", encoding="utf-8")

    compile_items = [
        f'    <Compile Include="{saxutils.escape(str(p))}" />'
        for p in cs_files
    ]
    compile_items.append(f'    <Compile Include="{saxutils.escape(str(stub_path))}" />')

    ref_items = [
        f'    <Reference Include="{saxutils.escape(dll.stem)}">\n'
        f'      <HintPath>{saxutils.escape(str(dll))}</HintPath>\n'
        f'      <Private>false</Private>\n'
        f'    </Reference>'
        for dll in ref_dlls
    ]

    analyzer_items = [
        f'    <Analyzer Include="{saxutils.escape(str(dll))}" />'
        for dll in analyzer_dlls
    ]

    proj_refs = [
        repo / "Cavern" / "Cavern.csproj",
        repo / "Cavern.Format" / "Cavern.Format.csproj",
        repo / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Cavernize.Logic.csproj",
    ]
    proj_ref_items = [
        f'    <ProjectReference Include="{saxutils.escape(str(pr))}" />'
        for pr in proj_refs
    ]

    csproj_xml = "\n".join([
        '<Project Sdk="Microsoft.NET.Sdk">',
        '  <PropertyGroup>',
        '    <TargetFramework>net8.0</TargetFramework>',
        '    <OutputType>Library</OutputType>',
        '    <LangVersion>preview</LangVersion>',
        '    <Nullable>disable</Nullable>',
        '    <ImplicitUsings>enable</ImplicitUsings>',
        '    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>',
        '    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>',
        '    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>',
        '    <DisableTransitiveFrameworkReferences>true</DisableTransitiveFrameworkReferences>',
        '    <GenerateDependencyFile>false</GenerateDependencyFile>',
        '    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>',
        '    <RootNamespace>CavernizeAndroid</RootNamespace>',
        '    <AssemblyName>CavernizeAndroid.TypeCheck</AssemblyName>',
        '    <DefineConstants>$(DefineConstants);ANDROID;__ANDROID__</DefineConstants>',
        '    <NoWarn>$(NoWarn);CA1416;CA1422</NoWarn>',
        '    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>',
        '  </PropertyGroup>',
        '  <ItemGroup>',
        *compile_items,
        '  </ItemGroup>',
        '  <ItemGroup>',
        *proj_ref_items,
        '  </ItemGroup>',
        '  <ItemGroup>',
        *ref_items,
        '  </ItemGroup>',
        '  <ItemGroup>',
        *analyzer_items,
        '  </ItemGroup>',
        '</Project>',
        '',
    ])
    shadow_csproj = shadow_dir / "CavernizeAndroid.TypeCheck.csproj"
    prev_xml = shadow_csproj.read_text(encoding="utf-8") if shadow_csproj.is_file() else None
    if prev_xml != csproj_xml:
        shutil.rmtree(shadow_dir / "obj", ignore_errors=True)
        shadow_csproj.write_text(csproj_xml, encoding="utf-8")
    return shadow_csproj, len(cs_files)


def _run_dotnet_check(
    dotnet_bin: str,
    shadow_dir: Path,
    shadow_csproj: Path,
    env: dict[str, str],
) -> tuple[int, str]:
    cmd = [
        dotnet_bin,
        "build",
        str(shadow_csproj),
        "-c",
        "Release",
        "-m:1",
        "/p:UseSharedCompilation=false",
        "--nologo",
        "-v:minimal",
    ]
    res = subprocess.run(cmd, cwd=str(shadow_dir), env=env, capture_output=True, text=True, timeout=240, check=False)
    combined = (res.stdout + "\n" + res.stderr).strip()
    return res.returncode, combined


def main() -> int:
    t0 = time.time()
    repo = _find_repo_root()
    dotnet_bin = "/root/.dotnet/dotnet" if Path("/root/.dotnet/dotnet").is_file() else "dotnet"
    cache_dir = Path.home() / ".cache" / "cavern-android-ref"
    shadow_dir = Path("/tmp/cavern-android-typecheck")

    try:
        ref_dlls = _ensure_netcore_refs(cache_dir) + _ensure_android_refs(cache_dir)
        analyzer_dlls = _ensure_netcore_analyzers(cache_dir, dotnet_bin)
    except Exception as exc:
        print(f"ANDROID REF FETCH ERROR: {exc}", file=sys.stderr)
        return 1

    roslyn_props = _ensure_roslyn_toolset(cache_dir)
    shadow_csproj, cs_count = _write_shadow_project(repo, shadow_dir, ref_dlls, analyzer_dlls, roslyn_props)

    env = os.environ.copy()
    env["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1"
    env["DOTNET_GCHeapHardLimit"] = "20000000"
    env["DOTNET_EnableDiagnostics"] = "0"

    roslyn_label = f"roslyn-{ROSLYN_VER}" if roslyn_props else "sdk-roslyn"
    print(f"Type-checking {cs_count} CavernizeAndroid C# files ({ANDROID_REF_PKG} {ANDROID_REF_VER}, {roslyn_label})...", flush=True)
    rc, combined = _run_dotnet_check(dotnet_bin, shadow_dir, shadow_csproj, env)

    # If custom Roslyn toolset crashed CoreCLR/MSBuild without CS errors, fall back cleanly to SDK Roslyn
    if rc != 0 and roslyn_props is not None and ": error CS" not in combined:
        print(f"Warning: {roslyn_label} failed unexpectedly (exit {rc}); retrying with SDK Roslyn...", flush=True)
        shadow_csproj, cs_count = _write_shadow_project(repo, shadow_dir, ref_dlls, analyzer_dlls, None)
        rc, combined = _run_dotnet_check(dotnet_bin, shadow_dir, shadow_csproj, env)

    if combined:
        print(combined)

    elapsed = time.time() - t0
    err_lines = sorted(set(ln.strip() for ln in combined.splitlines() if ": error CS" in ln or ": error MSB" in ln))
    print(
        f"[check_android_cs|files={cs_count}|exit={rc}|errors={len(err_lines)}|elapsed={elapsed:.2f}s]"
    )
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
