using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Cavernize.Logic.Models;

/// <summary>
/// Single persisted control-block descriptor for Wave D (W53.4 / B10) One UI 9 modular quick-panel layouts.
/// </summary>
public sealed class ModularBlockEntry {
    /// <summary>Stable block identifier (e.g., <c>stage</c>, <c>transport</c>, <c>quick_panel</c>).</summary>
    public string Id { get; set; } = "";

    /// <summary>Whether the block is visible in the active tab (always true when <see cref="Locked"/> is true).</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Essential control blocks cannot be hidden so playback/navigation can never be soft-locked.</summary>
    public bool Locked { get; set; }

    /// <summary>Pinned-top anchor blocks (such as Play Media Stage) remain at index 0 to preserve hero viewport weight.</summary>
    public bool PinnedTop { get; set; }

    /// <summary>Creates an independent copy of this block descriptor.</summary>
    public ModularBlockEntry Clone() => new() {
        Id = Id,
        Visible = Locked || Visible,
        Locked = Locked,
        PinnedTop = PinnedTop
    };
}

/// <summary>
/// Deterministic, forward-compatible modular block layout state for Play Now Playing and 3D Room tabs
/// (Wave D / W53.4 / B10 — One UI 9 editable quick-panel control blocks).
/// </summary>
public sealed class ModularQuickPanelLayout {
    /// <summary>Schema version stamped into persisted JSON.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Ordered list of modular control blocks.</summary>
    public List<ModularBlockEntry> Blocks { get; set; } = new();

    /// <summary>Canonical Play tab block identifiers.</summary>
    public static class PlayIds {
        public const string Stage = "stage";
        public const string Title = "title";
        public const string SeekBar = "seekbar";
        public const string Badges = "badges";
        public const string Transport = "transport";
        public const string Volume = "volume";
        public const string SpatialPills = "spatial_pills";
        public const string BottomActions = "bottom_actions";
    }

    /// <summary>Canonical 3D Room tab block identifiers.</summary>
    public static class RoomIds {
        public const string SignalChain = "signal_chain";
        public const string RoomCanvas = "room_canvas";
        public const string MiniTransport = "mini_transport";
        public const string XRayInspector = "xray_inspector";
        public const string QuickPanel = "quick_panel";
        public const string ChannelMixer = "channel_mixer";
    }

    /// <summary>Creates the canonical One UI 9 default layout for the Play tab.</summary>
    public static ModularQuickPanelLayout CreateDefaultPlayLayout() => new() {
        SchemaVersion = 1,
        Blocks = new List<ModularBlockEntry> {
            new() { Id = PlayIds.Stage, Visible = true, Locked = true, PinnedTop = true },
            new() { Id = PlayIds.Title, Visible = true, Locked = false, PinnedTop = false },
            new() { Id = PlayIds.SeekBar, Visible = true, Locked = false, PinnedTop = false },
            new() { Id = PlayIds.Badges, Visible = true, Locked = false, PinnedTop = false },
            new() { Id = PlayIds.Transport, Visible = true, Locked = true, PinnedTop = false },
            new() { Id = PlayIds.Volume, Visible = true, Locked = false, PinnedTop = false },
            new() { Id = PlayIds.SpatialPills, Visible = true, Locked = false, PinnedTop = false },
            new() { Id = PlayIds.BottomActions, Visible = true, Locked = true, PinnedTop = false }
        }
    };

    /// <summary>Creates the canonical One UI 9 default layout for the 3D Room tab.</summary>
    public static ModularQuickPanelLayout CreateDefaultRoomLayout() => new() {
        SchemaVersion = 1,
        Blocks = new List<ModularBlockEntry> {
            new() { Id = RoomIds.SignalChain, Visible = true, Locked = false, PinnedTop = false },
            new() { Id = RoomIds.RoomCanvas, Visible = true, Locked = true, PinnedTop = false },
            new() { Id = RoomIds.MiniTransport, Visible = true, Locked = false, PinnedTop = false },
            new() { Id = RoomIds.XRayInspector, Visible = true, Locked = false, PinnedTop = false },
            new() { Id = RoomIds.QuickPanel, Visible = true, Locked = false, PinnedTop = false },
            new() { Id = RoomIds.ChannelMixer, Visible = true, Locked = false, PinnedTop = false }
        }
    };

    /// <summary>Returns true when <paramref name="id"/> can move one slot upward.</summary>
    public bool CanMoveUp(string id) {
        int idx = IndexOf(id);
        if (idx <= 0) {
            return false;
        }
        if (Blocks[idx].PinnedTop || Blocks[idx - 1].PinnedTop) {
            return false;
        }
        return true;
    }

    /// <summary>Returns true when <paramref name="id"/> can move one slot downward.</summary>
    public bool CanMoveDown(string id) {
        int idx = IndexOf(id);
        if (idx < 0 || idx >= Blocks.Count - 1) {
            return false;
        }
        if (Blocks[idx].PinnedTop) {
            return false;
        }
        return true;
    }

    /// <summary>Moves block <paramref name="id"/> up by one position if allowed.</summary>
    public bool MoveUp(string id) {
        if (!CanMoveUp(id)) {
            return false;
        }
        int idx = IndexOf(id);
        (Blocks[idx - 1], Blocks[idx]) = (Blocks[idx], Blocks[idx - 1]);
        return true;
    }

    /// <summary>Moves block <paramref name="id"/> down by one position if allowed.</summary>
    public bool MoveDown(string id) {
        if (!CanMoveDown(id)) {
            return false;
        }
        int idx = IndexOf(id);
        (Blocks[idx], Blocks[idx + 1]) = (Blocks[idx + 1], Blocks[idx]);
        return true;
    }

    /// <summary>Toggles visibility of block <paramref name="id"/>. Locked essential blocks always remain visible.</summary>
    public bool SetVisible(string id, bool visible) {
        int idx = IndexOf(id);
        if (idx < 0) {
            return false;
        }
        ModularBlockEntry entry = Blocks[idx];
        if (entry.Locked) {
            entry.Visible = true;
            return false;
        }
        if (entry.Visible == visible) {
            return false;
        }
        entry.Visible = visible;
        return true;
    }

    /// <summary>Checks whether block <paramref name="id"/> is currently visible.</summary>
    public bool IsVisible(string id) {
        int idx = IndexOf(id);
        if (idx < 0) {
            return true;
        }
        return Blocks[idx].Locked || Blocks[idx].Visible;
    }

    /// <summary>Returns true when the layout differs from <paramref name="defaults"/> in order or visibility.</summary>
    public bool IsCustomized(ModularQuickPanelLayout defaults) {
        if (defaults == null || Blocks.Count != defaults.Blocks.Count) {
            return true;
        }
        for (int i = 0; i < Blocks.Count; i++) {
            if (!string.Equals(Blocks[i].Id, defaults.Blocks[i].Id, StringComparison.Ordinal) ||
                Blocks[i].Visible != defaults.Blocks[i].Visible) {
                return true;
            }
        }
        return false;
    }

    /// <summary>Resets block ordering and visibility to match <paramref name="defaults"/>.</summary>
    public void ResetFrom(ModularQuickPanelLayout defaults) {
        Blocks.Clear();
        if (defaults?.Blocks == null) {
            return;
        }
        for (int i = 0; i < defaults.Blocks.Count; i++) {
            Blocks.Add(defaults.Blocks[i].Clone());
        }
    }

    /// <summary>Serializes this layout to compact deterministic JSON.</summary>
    public string ToJson() {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions {
            WriteIndented = false
        });
    }

    /// <summary>
    /// Deserializes a saved JSON layout while strictly enforcing <paramref name="defaults"/> schema invariants:
    /// unknown/corrupt IDs are discarded, locked blocks stay visible, pinned-top blocks stay at index 0,
    /// and newly added blocks from future updates are appended seamlessly.
    /// </summary>
    public static ModularQuickPanelLayout FromJsonOrDefault(string json, ModularQuickPanelLayout defaults) {
        ModularQuickPanelLayout safeDefaults = defaults ?? CreateDefaultPlayLayout();
        if (string.IsNullOrWhiteSpace(json)) {
            ModularQuickPanelLayout fresh = new();
            fresh.ResetFrom(safeDefaults);
            return fresh;
        }

        try {
            ModularQuickPanelLayout parsed = JsonSerializer.Deserialize<ModularQuickPanelLayout>(json);
            if (parsed?.Blocks == null || parsed.Blocks.Count == 0) {
                ModularQuickPanelLayout fallback = new();
                fallback.ResetFrom(safeDefaults);
                return fallback;
            }

            Dictionary<string, ModularBlockEntry> canonicalById = new(StringComparer.Ordinal);
            for (int i = 0; i < safeDefaults.Blocks.Count; i++) {
                canonicalById[safeDefaults.Blocks[i].Id] = safeDefaults.Blocks[i];
            }

            List<ModularBlockEntry> pinned = new();
            List<ModularBlockEntry> regular = new();
            HashSet<string> seen = new(StringComparer.Ordinal);

            for (int i = 0; i < parsed.Blocks.Count; i++) {
                ModularBlockEntry raw = parsed.Blocks[i];
                if (raw == null || string.IsNullOrWhiteSpace(raw.Id)) {
                    continue;
                }
                if (!canonicalById.TryGetValue(raw.Id, out ModularBlockEntry spec) || !seen.Add(spec.Id)) {
                    continue;
                }
                ModularBlockEntry merged = new() {
                    Id = spec.Id,
                    Locked = spec.Locked,
                    PinnedTop = spec.PinnedTop,
                    Visible = spec.Locked || raw.Visible
                };
                if (spec.PinnedTop) {
                    pinned.Add(merged);
                } else {
                    regular.Add(merged);
                }
            }

            // Append any canonical blocks missing from older saved payloads
            for (int i = 0; i < safeDefaults.Blocks.Count; i++) {
                ModularBlockEntry spec = safeDefaults.Blocks[i];
                if (!seen.Add(spec.Id)) {
                    continue;
                }
                if (spec.PinnedTop) {
                    pinned.Add(spec.Clone());
                } else {
                    regular.Add(spec.Clone());
                }
            }

            List<ModularBlockEntry> finalBlocks = new(pinned.Count + regular.Count);
            finalBlocks.AddRange(pinned);
            finalBlocks.AddRange(regular);

            return new ModularQuickPanelLayout {
                SchemaVersion = 1,
                Blocks = finalBlocks
            };
        } catch {
            ModularQuickPanelLayout fallback = new();
            fallback.ResetFrom(safeDefaults);
            return fallback;
        }
    }

    int IndexOf(string id) {
        if (string.IsNullOrEmpty(id)) {
            return -1;
        }
        for (int i = 0; i < Blocks.Count; i++) {
            if (string.Equals(Blocks[i].Id, id, StringComparison.Ordinal)) {
                return i;
            }
        }
        return -1;
    }
}
