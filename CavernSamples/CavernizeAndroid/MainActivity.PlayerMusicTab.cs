using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

using Cavern.Format.Common;

using Cavernize.Logic.Models;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using Path = System.IO.Path;

namespace CavernizeAndroid;

public partial class MainActivity {
    sealed class RoundedArtworkView : View {
        readonly Paint bgPaint = new(PaintFlags.AntiAlias);
        readonly Paint bmpPaint = new(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
        readonly Paint rimPaint = new(PaintFlags.AntiAlias);
        readonly Android.Graphics.Path clipPath = new();
        readonly Matrix drawMatrix = new();

        Bitmap bitmap;
        Color accentTint = OneUiTheme.Primary;
        bool isPlayingState;

        public RoundedArtworkView(Context ctx) : base(ctx) {
            bgPaint.SetStyle(Paint.Style.Fill);
            rimPaint.SetStyle(Paint.Style.Stroke);
            rimPaint.StrokeWidth = Math.Max(1.5f, OneUiTheme.Dp(ctx, 1.0f));
            rimPaint.Color = Color.Argb(54, 255, 255, 255);
            Elevation = OneUiTheme.Dp(ctx, 14);
        }

        public void SetArtwork(Bitmap bmp, Color accent) {
            bitmap = bmp;
            accentTint = accent;
            Invalidate();
        }

        public void SetPlaying(bool playing) {
            if (isPlayingState == playing) {
                return;
            }
            isPlayingState = playing;
            float targetScale = playing ? 1.0f : 0.92f;
            float targetElev = OneUiTheme.Dp(Context, playing ? 16f : 7f);
            Animate()
                .ScaleX(targetScale)
                .ScaleY(targetScale)
                .SetDuration(340)
                .SetInterpolator(new Android.Views.Animations.DecelerateInterpolator(1.6f))
                .Start();
            Elevation = targetElev;
        }

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) {
            int maxW = MeasureSpec.GetSize(widthMeasureSpec);
            int target = Math.Min(maxW > 0 ? maxW : OneUiTheme.Dp(Context, 232f), OneUiTheme.Dp(Context, 232f));
            SetMeasuredDimension(target, target);
        }

        protected override void OnDraw(Canvas canvas) {
            base.OnDraw(canvas);
            float w = Width;
            float h = Height;
            if (w <= 0 || h <= 0) {
                return;
            }

            float rad = OneUiTheme.Dp(Context, 22f);
            RectF rect = new(0f, 0f, w, h);

            clipPath.Reset();
            clipPath.AddRoundRect(rect, rad, rad, Android.Graphics.Path.Direction.Cw);
            int save = canvas.Save();
            canvas.ClipPath(clipPath);

            bgPaint.Color = Color.Argb(255, 16, 18, 26);
            canvas.DrawRect(rect, bgPaint);

            if (bitmap != null && !bitmap.IsRecycled && bitmap.Width > 0 && bitmap.Height > 0) {
                float bw = bitmap.Width;
                float bh = bitmap.Height;
                float scale = Math.Max(w / bw, h / bh);
                float dx = (w - bw * scale) * 0.5f;
                float dy = (h - bh * scale) * 0.5f;
                drawMatrix.Reset();
                drawMatrix.PostScale(scale, scale);
                drawMatrix.PostTranslate(dx, dy);
                canvas.DrawBitmap(bitmap, drawMatrix, bmpPaint);
            } else {
                using LinearGradient grad = new(0f, 0f, w, h,
                    [Color.Argb(215, accentTint.R, accentTint.G, accentTint.B).ToArgb(), Color.Rgb(16, 18, 25).ToArgb()],
                    [0f, 1f], Shader.TileMode.Clamp!);
                bgPaint.SetShader(grad);
                canvas.DrawRect(rect, bgPaint);
                bgPaint.SetShader(null);
            }
            canvas.RestoreToCount(save);

            canvas.DrawRoundRect(
                new RectF(0.75f, 0.75f, w - 0.75f, h - 0.75f),
                rad, rad, rimPaint);
        }
    }

    RoundedArtworkView musicArtworkView;
    TextView musicTitleText, musicArtistText, musicSpatialBadgeText, musicTelemetryText;
    LinearLayout musicBadgeRow, musicSpatialPill, musicTrackBtn;
    TextView musicTrackBtnText;
    OneUiWaveMediaSeekBar musicWaveSeekBar;
    OneUiSeekBar musicVolSeekBar;
    FrameLayout musicPlayCircleBtn;
    OneUiIconView musicPlayIcon, musicLoopIcon;
    LinearLayout musicLoopBtn, musicChipHrtf, musicChipCavern, musicChipMatrix;

    LinearLayout BuildMusicPlayerTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetGravity(GravityFlags.CenterHorizontal);
        page.SetPadding(Dp(18), Dp(6), Dp(18), Dp(116));

        // 1. Elevated Apple-Music Style Cover Artwork Card
        musicArtworkView = new RoundedArtworkView(this) {
            ScaleX = 0.92f,
            ScaleY = 0.92f
        };
        musicArtworkView.Click += (_, _) => {
            if (LoadedFile == null) {
                LaunchFilePicker(ReqOpenMedia, allowMultiple: false);
            } else {
                TogglePlayPause();
            }
        };
        page.AddView(musicArtworkView, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(6),
            BottomMargin = Dp(16)
        });

        // 2. Spatial Codec Badge + Inline Container Track Switcher Pill
        musicBadgeRow = HRow();
        musicBadgeRow.SetGravity(GravityFlags.CenterHorizontal | GravityFlags.CenterVertical);

        musicSpatialPill = HRow();
        musicSpatialPill.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
        musicSpatialPill.Background = OneUiTheme.CreateGlassCardDrawable(
            this, OneUiTheme.WithAlpha(ColAccent, 52), OneUiTheme.PillCornerDp, OneUiTheme.WithAlpha(ColAccent, 140));
        OneUiIconView badgeIcon = new(this, OneUiIconType.Sparkles, ColCyan);
        musicSpatialBadgeText = Label(S["PlayerBadgeSpatial"], 11f, ColText);
        musicSpatialBadgeText.SetIncludeFontPadding(false);
        musicSpatialBadgeText.SetSingleLine(true);
        musicSpatialBadgeText.SetTypeface(null, TypefaceStyle.Bold);
        musicSpatialPill.AddView(badgeIcon, new LinearLayout.LayoutParams(Dp(12), Dp(12)) { RightMargin = Dp(5) });
        musicSpatialPill.AddView(musicSpatialBadgeText);
        musicBadgeRow.AddView(musicSpatialPill);

        musicTrackBtn = HRow();
        musicTrackBtn.Visibility = ViewStates.Gone;
        musicTrackBtn.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
        musicTrackBtn.Background = OneUiTheme.CreateGlassCardDrawable(
            this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
        OneUiTheme.AttachRecoil(musicTrackBtn, 0.94f);
        musicTrackBtnText = Label(S["PlayerSwitchTrack"], 11f, OneUiTheme.TextSecondary);
        musicTrackBtnText.SetIncludeFontPadding(false);
        musicTrackBtnText.SetSingleLine(true);
        OneUiIconView trackChev = new(this, OneUiIconType.ChevronDown, OneUiTheme.TextSub);
        musicTrackBtn.AddView(musicTrackBtnText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(4) });
        musicTrackBtn.AddView(trackChev, new LinearLayout.LayoutParams(Dp(11), Dp(11)));
        musicTrackBtn.Click += (_, _) => ShowTrackSelectorDialog();
        musicBadgeRow.AddView(musicTrackBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(8) });

        page.AddView(musicBadgeRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(10) });

        // 3. Editorial Track Title & Artist
        musicTitleText = new TextView(this) {
            Text = S["PlayerEmptyTitle"],
            TextSize = 20f
        };
        musicTitleText.SetTextColor(ColText);
        musicTitleText.SetTypeface(null, TypefaceStyle.Bold);
        musicTitleText.Gravity = GravityFlags.CenterHorizontal;
        musicTitleText.SetMaxLines(2);
        musicTitleText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        page.AddView(musicTitleText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(4) });

        musicArtistText = new TextView(this) {
            Text = S["PlayerEmptySub"],
            TextSize = 13.5f
        };
        musicArtistText.SetTextColor(OneUiTheme.TextSub);
        musicArtistText.Gravity = GravityFlags.CenterHorizontal;
        musicArtistText.SetSingleLine(true);
        musicArtistText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        page.AddView(musicArtistText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(16) });

        // 4. Samsung One UI 9 Wave Media Seekbar (harmonized with cover art accent)
        musicWaveSeekBar = new OneUiWaveMediaSeekBar(this) {
            Max = 1000,
            CurrentTimeText = "00:00",
            TotalTimeText = "00:00"
        };
        musicWaveSeekBar.SetThemeByColor(OneUiTheme.Primary);
        musicWaveSeekBar.StartTrackingTouch += () => isUserSeeking = true;
        musicWaveSeekBar.ProgressChanged += (prog, fromUser) => {
            if (fromUser && playerEngine.TotalSeconds > 0) {
                double preview = prog / 1000.0 * playerEngine.TotalSeconds;
                string curStr = $"{TimeSpan.FromSeconds(preview):mm\\:ss}";
                string totStr = $"{TimeSpan.FromSeconds(playerEngine.TotalSeconds):mm\\:ss}";
                musicWaveSeekBar.CurrentTimeText = curStr;
                musicWaveSeekBar.TotalTimeText = totStr;
                musicWaveSeekBar.Invalidate();
                if (playbackSeekBar != null) {
                    playbackSeekBar.Progress = prog;
                    playbackSeekBar.CurrentTimeText = curStr;
                    playbackSeekBar.TotalTimeText = totStr;
                    playbackSeekBar.Invalidate();
                }
            }
        };
        musicWaveSeekBar.StopTrackingTouch += () => {
            isUserSeeking = false;
            if (playerEngine.TotalSeconds > 0) {
                SeekAbsoluteSeconds(musicWaveSeekBar.Progress / 1000.0 * playerEngine.TotalSeconds);
            }
        };
        page.AddView(musicWaveSeekBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(12) });

        // 5. Spotify / YTM Tactile Transport Row
        LinearLayout transportRow = HRow();
        transportRow.SetGravity(GravityFlags.CenterHorizontal | GravityFlags.CenterVertical);

        musicLoopBtn = MakeCircleIconBtn(OneUiIconType.RepeatLoop, 42, false, () => {
            bool next = !playerEngine.Loop;
            playerEngine.Loop = next;
            if (loopSwitch != null) {
                loopSwitch.Checked = next;
            }
            RefreshMusicPlayerControlsUi();
        }, out musicLoopIcon);

        LinearLayout rewBtn = MakeCircleIconBtn(OneUiIconType.Rewind10, 48, false, () => SeekRelative(-10), out _);

        musicPlayCircleBtn = new FrameLayout(this);
        int playSz = Dp(66);
        musicPlayCircleBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, 99f);
        musicPlayCircleBtn.Elevation = Dp(8);
        OneUiTheme.AttachRecoil(musicPlayCircleBtn, 0.91f);
        musicPlayIcon = new OneUiIconView(this, OneUiIconType.Play, ColText);
        musicPlayCircleBtn.AddView(musicPlayIcon, new FrameLayout.LayoutParams(Dp(26), Dp(26)) { Gravity = GravityFlags.Center });
        musicPlayCircleBtn.Click += (_, _) => TogglePlayPause();

        LinearLayout fwdBtn = MakeCircleIconBtn(OneUiIconType.Forward10, 48, false, () => SeekRelative(10), out _);
        LinearLayout stopCircleBtn = MakeCircleIconBtn(OneUiIconType.Stop, 42, false, StopPlayback, out _);

        transportRow.AddView(musicLoopBtn, new LinearLayout.LayoutParams(Dp(42), Dp(42)) { RightMargin = Dp(10) });
        transportRow.AddView(rewBtn, new LinearLayout.LayoutParams(Dp(48), Dp(48)) { RightMargin = Dp(12) });
        transportRow.AddView(musicPlayCircleBtn, new LinearLayout.LayoutParams(playSz, playSz) { RightMargin = Dp(12) });
        transportRow.AddView(fwdBtn, new LinearLayout.LayoutParams(Dp(48), Dp(48)) { RightMargin = Dp(10) });
        transportRow.AddView(stopCircleBtn, new LinearLayout.LayoutParams(Dp(42), Dp(42)));
        page.AddView(transportRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(16) });

        // 6. One UI 9 Master Volume Capsule Slider (synced bidirectionally with Sala 3D)
        (musicVolSeekBar, _) = AddSliderRow(page, S["MasterVol"], 0, 200, (int)Math.Round(store.PlayerVolume * 100),
            v => SetMasterVolumePercent(v), "%", defaultValue: 100);

        // 7. PowerAMP Technical Telemetry Card & Instant Spatial Shortcuts
        LinearLayout dspCard = Card();
        musicTelemetryText = Label(S["NoFile"], 11.5f, OneUiTheme.PeachText);
        musicTelemetryText.Gravity = GravityFlags.CenterHorizontal;
        musicTelemetryText.SetPadding(0, 0, 0, Dp(10));
        dspCard.AddView(musicTelemetryText);

        LinearLayout chipRow = HRow();
        chipRow.SetPadding(0, 0, 0, Dp(10));
        musicChipHrtf = MakeDspQuickChip(S["BentoHrtf"], () => {
            store.HeadphoneVirtualizer = !store.HeadphoneVirtualizer;
            playerEngine.HeadphoneVirtualizer = store.HeadphoneVirtualizer;
            refreshBentoTilesUi?.Invoke();
            RefreshMusicPlayerControlsUi();
        });
        musicChipCavern = MakeDspQuickChip("Cavernize 3D", () => {
            store.CavernizeUpmix = !store.CavernizeUpmix;
            if (cavernizeUpmixSwitch != null) cavernizeUpmixSwitch.Checked = store.CavernizeUpmix;
            ReattachPlayerTrack();
            RefreshMusicPlayerControlsUi();
        });
        musicChipMatrix = MakeDspQuickChip(S["PlayerQuickMatrix"], () => {
            store.MatrixUpmixing = !store.MatrixUpmixing;
            if (matrixUpmixSwitch != null) matrixUpmixSwitch.Checked = store.MatrixUpmixing;
            ReattachPlayerTrack();
            RefreshMusicPlayerControlsUi();
        });

        chipRow.AddView(musicChipHrtf, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        chipRow.AddView(musicChipCavern, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.15f) { RightMargin = Dp(6) });
        chipRow.AddView(musicChipMatrix, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        dspCard.AddView(chipRow);

        Button openRoomBtn = PillButton(S["PlayerOpenRoom"], () => SwitchTab(1), active: false);
        dspCard.AddView(openRoomBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        page.AddView(dspCard);
        RefreshMusicPlayerUi();
        return page;
    }

    LinearLayout MakeCircleIconBtn(OneUiIconType icon, int sizeDp, bool primary, Action onClick, out OneUiIconView iconOut) {
        LinearLayout btn = new(this) { Orientation = Orientation.Horizontal };
        btn.SetGravity(GravityFlags.Center);
        btn.Background = primary
            ? OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, 99f)
            : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceElevated, 99f);
        OneUiTheme.AttachRecoil(btn, 0.92f);
        int icoPx = Dp(Math.Max(16, (int)(sizeDp * 0.44f)));
        iconOut = new OneUiIconView(this, icon, ColText);
        btn.AddView(iconOut, new LinearLayout.LayoutParams(icoPx, icoPx));
        btn.Click += (_, _) => onClick?.Invoke();
        return btn;
    }

    LinearLayout MakeDspQuickChip(string text, Action onClick) {
        LinearLayout chip = HRow();
        chip.SetGravity(GravityFlags.Center);
        chip.SetMinimumHeight(Dp(36));
        chip.SetPadding(Dp(6), Dp(6), Dp(6), Dp(6));
        OneUiTheme.AttachRecoil(chip, 0.94f);
        TextView tv = Label(text, 11.5f, ColText);
        tv.SetIncludeFontPadding(false);
        tv.SetSingleLine(true);
        tv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        tv.SetTypeface(null, TypefaceStyle.Bold);
        chip.AddView(tv);
        chip.Tag = tv;
        chip.Click += (_, _) => onClick?.Invoke();
        return chip;
    }

    void RefreshMusicPlayerUi() {
        if (musicArtworkView == null) {
            return;
        }
        Color accent = currentMediaMeta?.PrimaryAccent ?? OneUiTheme.Primary;
        Color accent2 = currentMediaMeta?.SecondaryAccent ?? OneUiTheme.Secondary;
        backdropView?.SetMediaAccent(
            LoadedFile != null ? accent : null,
            LoadedFile != null ? accent2 : null);

        if (LoadedFile != null && currentMediaMeta?.ArtworkBitmap != null && !currentMediaMeta.ArtworkBitmap.IsRecycled) {
            musicArtworkView.SetArtwork(currentMediaMeta.ArtworkBitmap, accent);
        } else {
            defaultFallbackCover ??= MediaMetadataHelper.CreateSpatialFallbackArtwork("Cavernize", OneUiTheme.PrimaryHigh, OneUiTheme.Secondary, 512);
            musicArtworkView.SetArtwork(defaultFallbackCover, OneUiTheme.Primary);
        }

        if (LoadedFile == null || selectedTrack == null) {
            musicTitleText.Text = S["PlayerEmptyTitle"];
            musicArtistText.Text = S["PlayerEmptySub"];
            musicSpatialBadgeText.Text = S["PlayerBadgeSpatial"];
            musicTrackBtn.Visibility = ViewStates.Gone;
            musicWaveSeekBar?.SetThemeByColor(OneUiTheme.Primary);
            musicPlayCircleBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, 99f);
        } else {
            musicTitleText.Text = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
                ? currentMediaMeta.Title
                : Path.GetFileNameWithoutExtension(LoadedFile.Path);

            string sub = string.Join(" — ", new[] { currentMediaMeta?.Artist, currentMediaMeta?.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
            musicArtistText.Text = !string.IsNullOrWhiteSpace(sub)
                ? sub
                : selectedTrack.FormatHeader;

            int beds = trackSourceItems.Count(x => x.IsBed);
            int objs = trackSourceItems.Count(x => x.IsDynamicObject);
            musicSpatialBadgeText.Text = objs > 0
                ? $"Spatial 3D • {objs} Obj"
                : selectedTrack.Codec.ToString().ToUpperInvariant();

            if (LoadedFile.Tracks.Count > 1) {
                int idx = Math.Max(0, LoadedFile.Tracks.ToList().IndexOf(selectedTrack));
                musicTrackBtnText.Text = $"{S["TrackLabel"].TrimEnd(':')} {idx + 1}/{LoadedFile.Tracks.Count}";
                musicTrackBtn.Visibility = ViewStates.Visible;
            } else {
                musicTrackBtn.Visibility = ViewStates.Gone;
            }

            Color playAccent = currentMediaMeta?.HasEmbeddedArtwork == true ? accent : ColAccent;
            musicWaveSeekBar?.SetThemeByColor(playAccent);
            musicPlayCircleBtn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, playAccent, 99f);
            musicSpatialPill.Background = OneUiTheme.CreateGlassCardDrawable(
                this, OneUiTheme.WithAlpha(playAccent, 56), OneUiTheme.PillCornerDp, OneUiTheme.WithAlpha(playAccent, 150));
        }

        RefreshMusicPlayerControlsUi();
    }

    void RefreshMusicPlayerControlsUi() {
        if (musicTelemetryText == null) {
            return;
        }
        bool isPlaying = playerEngine?.IsPlaying ?? false;
        musicArtworkView?.SetPlaying(isPlaying);
        if (musicPlayIcon != null) {
            musicPlayIcon.IconType = isPlaying ? OneUiIconType.Pause : OneUiIconType.Play;
        }

        if (musicLoopBtn != null && musicLoopIcon != null) {
            bool loop = playerEngine?.Loop ?? false;
            musicLoopBtn.Background = loop
                ? OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.WithAlpha(ColAccent, 90), 99f, ColAccent)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceElevated, 99f);
            musicLoopIcon.IconColor = loop ? ColText : OneUiTheme.TextSub;
        }

        StyleQuickChip(musicChipHrtf, store.HeadphoneVirtualizer, OneUiTheme.Primary);
        StyleQuickChip(musicChipCavern, store.CavernizeUpmix, OneUiTheme.FunctionalIndigo);
        StyleQuickChip(musicChipMatrix, store.MatrixUpmixing, OneUiTheme.FunctionalCyan);

        if (selectedTrack == null || playerEngine == null) {
            musicTelemetryText.Text = $"{RenderTarget?.Name ?? "Binaural"} • {Math.Round(store.PlayerVolume * 100)}%";
        } else {
            int sr = currentMediaMeta?.EmbeddedSampleRate > 0 ? currentMediaMeta.EmbeddedSampleRate : 48000;
            int beds = trackSourceItems.Count(x => x.IsBed);
            int objs = trackSourceItems.Count(x => x.IsDynamicObject);
            string srStr = $"{sr / 1000.0:0.#} kHz";
            string chStr = objs > 0 ? $"{beds} ch + {objs} obj" : $"{trackSourceItems.Count} ch";
            musicTelemetryText.Text = $"{selectedTrack.Codec} • {srStr} • {chStr} • {Math.Round(store.PlayerVolume * 100)}%";
        }
    }

    void StyleQuickChip(LinearLayout chip, bool active, Color accent) {
        if (chip == null) {
            return;
        }
        chip.Background = OneUiTheme.CreateGlassCardDrawable(
            this,
            active ? OneUiTheme.WithAlpha(accent, 95) : OneUiTheme.SurfaceSubTile,
            OneUiTheme.PillCornerDp,
            active ? accent : null);
        if (chip.Tag is TextView tv) {
            tv.SetTextColor(active ? ColText : OneUiTheme.TextSecondary);
        }
    }

    void ShowTrackSelectorDialog() {
        if (LoadedFile == null || LoadedFile.Tracks.Count <= 1) {
            return;
        }
        LinearLayout list = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        for (int i = 0; i < LoadedFile.Tracks.Count; i++) {
            CavernizeTrack tr = LoadedFile.Tracks[i];
            bool isSel = tr == selectedTrack;
            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            row.Background = isSel
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 80), 16f)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 16f);
            OneUiTheme.AttachRecoil(row, 0.97f);

            TextView tv = Label($"#{i + 1}: {tr}", 13f, isSel ? ColText : OneUiTheme.TextSecondary);
            tv.SetTypeface(null, isSel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            row.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            row.Click += (_, _) => {
                dlg?.Dismiss();
                if (SelectedTrack != tr) {
                    SelectedTrack = tr;
                }
            };
            list.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });
        }

        dlg = OneUiModalDialog.Show(
            this,
            S["SelectTrackHeader"],
            null,
            icon: OneUiIconType.Queue,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["DismissBtn"],
            customContent: list);
    }

    void SetMasterVolumePercent(int percent) {
        percent = Math.Clamp(percent, 0, 200);
        float vol = percent / 100f;
        store.PlayerVolume = vol;
        playerEngine.Volume = vol;

        if (seekBarVolume != null && seekBarVolume.Progress != percent) {
            seekBarVolume.Progress = percent;
            seekBarVolume.ValueText = $"{percent}%";
            seekBarVolume.Invalidate();
        }
        if (musicVolSeekBar != null && musicVolSeekBar.Progress != percent) {
            musicVolSeekBar.Progress = percent;
            musicVolSeekBar.ValueText = $"{percent}%";
            musicVolSeekBar.Invalidate();
        }
        RefreshMusicPlayerControlsUi();
    }
}
