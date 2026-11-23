# OmniAgent Companion — Termux Permission, Smart Debian Launcher & Language Switcher

Resolve the runtime `com.termux.permission.RUN_COMMAND` authorization failure observed on device, automate waking the full-agent daemon inside Debian PRoot or Termux with zero manual path guesswork, and add an instant system-following `Auto / PT-BR / EN` language switcher to the OmniAgent One UI 9 dashboard.

---

## User Review & Critical Decisions

> [!IMPORTANT]
> The following decisions were confirmed during Phase 1 and govern this update:

- **Confirmed Scope**: Fix the Termux `RUN_COMMAND` runtime permission error, add smart Debian PRoot / Termux daemon wake automation, add a guided 1-tap setup helper (for `allow-external-apps=true` and first-time Debian daemon install), and add safe-area `WindowInsets` polish for smooth edge-to-edge scrolling without altering the existing card layout.
- **Confirmed Wake Behavior (Recommended Smart Hybrid)**: Because the user does not want to manually choose between Debian PRoot and Termux, tapping **Wake Termux** will automatically detect and launch the agent inside **Debian PRoot** (where Antigravity and the repository live) or Termux native, falling back gracefully to opening Termux with a one-tap setup sheet if not yet initialized.
- **Confirmed Language Preference**: Default to **following the device's system language** (reading Android's primary configuration locale list for reliable PT-BR detection), paired with a compact header toggle button to switch live between **Auto**, **PT-BR**, and **EN** without restarting the app.

---

## 1. Overview & Core Concept

- **What It Does**: Turns the **Wake Termux** action and overall OmniAgent Companion experience into a frictionless one-tap control center. Instead of failing silently or throwing a `SecurityException` when Android blocks Termux's dangerous `RUN_COMMAND` permission, OmniAgent proactively checks and requests the runtime permission, automatically retries waking the daemon once granted, and handles both Debian PRoot and Termux setups transparently.
- **Target Audience / Persona**: The device owner managing Cavernize and the "Antigravity 2" full-agent workflow directly from their Samsung S25 Ultra (Android 17) alongside AI Studio.
- **Key Value**: Eliminates permission crashes, removes terminal path confusion between Termux and Debian PRoot, and keeps the interface immediately readable in the user's preferred language.

---

## 2. User Experience & Visual Design

### Key User Flows

1. **One-Tap Wake Termux & Permission Flow**:
   - User taps **Wake Termux / Acordar Termux** on the Status card.
   - OmniAgent checks whether Termux is installed and whether Android has granted `com.termux.permission.RUN_COMMAND`.
   - If permission is missing, Android's native permission dialog appears immediately; as soon as the user taps **Allow**, OmniAgent automatically retries launching the daemon without needing a second tap.
   - If the user previously denied permission permanently, OmniAgent displays a clean One UI 9 bottom sheet with a direct **Open App Settings** button and a **Copy Termux Setup Command** button.
   - Once authorized, OmniAgent dispatches a smart bash launcher to Termux that starts the background daemon inside **Debian PRoot** (or Termux native if installed there) and logs clear confirmation in **Live Agent Activity**.

2. **One-Tap Termux / Debian Setup Sheet**:
   - Accessible from the Bridge card or automatically offered if the Termux daemon hasn't been configured yet.
   - Displays a single copyable setup command that enables `allow-external-apps=true` in Termux, bridges Termux to the Debian PRoot daemon, and starts it in the background—plus an **Open Termux** button so the user only pastes once.

3. **Live Language Switcher (`Auto` / `PT-BR` / `EN`)**:
   - Compact pill button sits neatly aligned on the top-right of the `OmniAgent` header row (`Auto`, `PT`, or `EN`).
   - Tapping cycles through **Auto (System)** → **PT-BR** → **EN**, persisting the choice immediately and refreshing all titles, cards, badges, buttons, and bottom sheets in place without losing service state or activity logs.

### Visual Identity & Theme

- **Aesthetic Direction**: Authentic Samsung One UI 9 AMOLED dark utility—calm, high-contrast, rounded 24–26dp cards, zero visual clutter, and zero stock Android `AlertDialog` or `Spinner` chrome.
- **Color Palette & Mood**: Preserves the existing AMOLED palette: deep canvas `#09090B`, card surface `#16161B`, elevated controls `#202028`, hairline border `#2A2A35`, primary blue `#2F7CF6`, live green `#22C55E`, stop red `#EF4444`, primary ink `#F4F4F6`, and muted slate `#9CA3AF`.
- **Typography & Hierarchy**: Bold 28sp `OmniAgent` header paired with a compact pill-shaped language selector on the right; 15sp bold card titles; 13sp action labels; 11sp monospace activity journal.
- **Component Styling & Layout**: Horizontal action rows preserve `BaselineAligned = false` anti-clipping; dynamic `WindowInsets` (`SystemBars` + `DisplayCutout`) keep scrolled content and bottom sheets clear of the status bar, camera cutout, and bottom navigation gesture bar.

### Interactive Feedback & Motion

- Immediate button feedback via toast + timestamped entries in **Live Agent Activity**.
- Clear distinction between **permission requested**, **command dispatched**, **Termux not installed**, and **external apps disabled** states so the user never sees raw Java exception stack traces as the primary message.
- Relay health test action shows live latency and Worker version (`v4`) directly in the Bridge card / sheet.

---

## 3. Key Product Decisions & Trade-Offs

- **Decision 1: Runtime `RUN_COMMAND` Permission Handshake + Auto-Retry**
  - *Chosen Approach*: Explicitly check `CheckSelfPermission("com.termux.permission.RUN_COMMAND")` before invoking Termux's `RunCommandService`, call `RequestPermissions` on demand, and auto-dispatch the pending wake command inside `OnRequestPermissionsResult` once granted.
  - *Why*: Termux defines `com.termux.permission.RUN_COMMAND` with `protectionLevel="dangerous"`; manifest declaration alone always throws `SecurityException` on modern Android. Auto-retrying removes extra taps.
  - *Alternatives Considered*: Only showing instructions to go into Android Settings manually (slower and error-prone).

- **Decision 2: Smart Auto-Detect Hybrid Launcher (Debian PRoot + Termux)**
  - *Chosen Approach*: Dispatch `/data/data/com.termux/files/usr/bin/bash` with a compact auto-detect command that checks for Termux's `~/.omni_agent/start.sh`, then falls back to `proot-distro login debian -- bash /root/.omni_agent/start.sh`, and also enhance the installer script to enable `allow-external-apps=true` when run in Termux and generate the bridge launcher automatically.
  - *Why*: Users should not need to remember whether their daemon lives inside Debian PRoot or Termux home; both environments work automatically with one tap.
  - *Alternatives Considered*: Hardcoding only `/data/data/com.termux/files/home/.omni_agent/start.sh` (fails when the user ran the installer inside Debian PRoot).

- **Decision 3: Reliable System Locale Resolution + Persisted Override**
  - *Chosen Approach*: Resolve system language from Android's `Resources.Configuration.Locales` (API 24+) before falling back to `Java.Util.Locale.Default`, combined with a persisted `auto | pt | en` preference exposed via a header pill button.
  - *Why*: Ensures Portuguese-configured devices show PT-BR immediately under `Auto`, while giving instant manual control if the device is set to another language.
  - *Alternatives Considered*: Hardcoding PT-BR only (ignored user's preference to follow phone language with an optional switcher).

---

## 4. Technical Architecture & Data Strategy *(Technical Reference)*

### Architecture & Component Diagram

```text
┌─────────────────────────────────────────────────────────────────────────┐
│                    OmniAgent Companion (MainActivity)                   │
│  ┌──────────────────────────────┐   ┌────────────────────────────────┐  │
│  │ Header + Live Locale Pill    │   │ WindowInsets Safe-Area Wrapper │  │
│  │ (Auto -> Resources.Locales   │   │ (SystemBars + DisplayCutout)   │  │
│  │  / PT-BR / EN)               │   └────────────────────────────────┘  │
│  └──────────────┬───────────────┘                                       │
│                 │ persists `ui_language`                                │
│  ┌──────────────▼───────────────┐   ┌────────────────────────────────┐  │
│  │ Status Card                  │   │ One UI 9 Bottom Sheets         │  │
│  │ • Start / Stop Agent         │   │ • Relay & Key (+ Test Health)  │  │
│  │ • Wake Termux ───────────────┼──▶│ • Termux Setup Helper (Copy +  │  │
│  │   (Runtime Permission Gate)  │   │   Open Settings / Open Termux) │  │
│  └──────────────┬───────────────┘   │ • GitHub Push Config           │  │
│                 │                   └────────────────────────────────┘  │
└─────────────────┼───────────────────────────────────────────────────────┘
                  │ CheckSelfPermission / RequestPermissions
                  ▼
┌─────────────────────────────────────────────────────────────────────────┐
│              OmniAgentForegroundService (channel: android)              │
│  • LaunchTermuxSmartDaemon():                                           │
│      1. Verifies Termux installed & RUN_COMMAND permission granted      │
│      2. Dispatches Termux RUN_COMMAND -> bash -lc "<auto-detect>"       │
│         ├─ Native ~/.omni_agent/start.sh (if present)                   │
│         └─ proot-distro login debian -- bash /root/.omni_agent/start.sh │
│  • Preserves E2E Cloudflare Hub v4 polling, GitHub Push & APK Install   │
└─────────────────┬───────────────────────────────────────────────────────┘
                  │ Intent: com.termux.RUN_COMMAND (background)
                  ▼
┌─────────────────────────────────────────────────────────────────────────┐
│             Termux + Debian PRoot (Antigravity 2 Daemon)                │
│  • allow-external-apps=true in ~/.termux/termux.properties              │
│  • Installer auto-creates background launcher & connects channel=termux │
└─────────────────────────────────────────────────────────────────────────┘
```

### Data Model & State

- **Persisted Preferences (`OmniSettingsStore`)**:
  - `ui_language`: `"auto"` (default) | `"pt"` | `"en"`.
  - Existing encrypted Keystore secrets (`pair_secret_enc`, `github_pat_enc`) and settings (`relay_url`, `github_repo`, `github_branch`, `security_mode`, `service_enabled`, `termux_script`) remain untouched and 100% backwards-compatible.
- **Transient Activity State**:
  - `pendingTermuxWake`: tracks an in-flight Wake Termux request while waiting for Android's `OnRequestPermissionsResult` callback so the launch executes automatically once granted.
  - Live insets (`topInsetPx`, `bottomInsetPx`) stored from `WindowInsets` listener and applied cleanly to the scroll viewport and modal overlay.

### Interactive Component & State Mapping

- **Language Pill Click** → cycles `store.UiLanguage` (`auto` → `pt` → `en` → `auto`) → resolves effective `isPt` → rebuilds UI labels & refreshes live status/activity without stopping `OmniAgentForegroundService`.
- **Wake Termux Click** → checks Termux package presence → checks `com.termux.permission.RUN_COMMAND` runtime permission:
  - *Missing*: sets `pendingTermuxWake = true` and calls `RequestPermissions`. On grant, auto-dispatches smart hybrid daemon launch; on denial, opens the Termux Setup bottom sheet with **Open App Settings** + **Copy Setup Command** + **Open Termux**.
  - *Granted*: immediately dispatches the smart hybrid Debian PRoot / Termux launcher via `OmniAgentForegroundService` and logs result to **Live Agent Activity** (offering the setup sheet if `allow-external-apps=true` has not been enabled yet).
- **Relay & Key Sheet (`Test` Action)** → calls `GET <worker>/health` asynchronously, displaying verified Worker version (`v4`) and round-trip latency directly inside the sheet without freezing the UI thread.
- **Remote AI Command (`launch_termux_daemon`)** → reuses the exact same smart hybrid launcher and returns clear structured diagnostics (`permissionGranted`, `termuxInstalled`, `hint`) if Android still needs one-time permission approval on screen.

### Verification Plan

- Run full **7-layer local CI-parity check** (including Roslyn + Android platform analyzers `CA1416`/`CA1422` across both CavernizeAndroid and OmniAgentAndroid) with 0 errors and 0 warnings.
- Run Python unit tests, Node tests, universal plugin verification, `error_learner --audit-lessons`, and applet build/lint checks after implementation.
