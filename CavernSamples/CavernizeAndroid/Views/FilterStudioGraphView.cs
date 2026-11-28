using Android.Content;
using Android.Graphics;
using Android.Views;

using Cavern.Filters;
using Cavernize.Logic.Spatial.Adapters;

using CavernizeAndroid.Theme;

namespace CavernizeAndroid.Views;

/// <summary>
/// Interactive 120 Hz zero-allocation logarithmic (20 Hz..20 kHz) parametric EQ canvas for FilterStudio (W57.3).
/// Draws live spectrum overlay, composite Cytomic SVF / Nyquist-decramped magnitude curve, optional phase overlay,
/// and touch-draggable parametric band nodes.
/// </summary>
public sealed class FilterStudioGraphView : View {
    const double MinFreqHz = 20.0;
    const double MaxFreqHz = 20000.0;
    const double MinGainDb = -18.0;
    const double MaxGainDb = 18.0;
    const int CurveSamples = 128;

    static readonly double LogMin = Math.Log10(MinFreqHz);
    static readonly double LogMax = Math.Log10(MaxFreqHz);
    static readonly double LogSpan = LogMax - LogMin;
    static readonly double[] GridFreqs = [50, 100, 200, 500, 1000, 2000, 5000, 10000];
    static readonly string[] GridFreqLabels = ["50", "100", "200", "500", "1k", "2k", "5k", "10k"];
    static readonly double[] GridGains = [-12, -6, 0, 6, 12];

    readonly float dp;
    readonly FilterStudioPostProcessingStage stage;
    readonly float[] spectrumBuf = new float[FilterStudioPostProcessingStage.SpectrumBinCount];
    readonly Paint bgPaint = new(PaintFlags.AntiAlias);
    readonly Paint borderPaint = new(PaintFlags.AntiAlias);
    readonly Paint gridPaint = new(PaintFlags.AntiAlias);
    readonly Paint zeroLinePaint = new(PaintFlags.AntiAlias);
    readonly Paint labelPaint = new(PaintFlags.AntiAlias);
    readonly Paint spectrumFillPaint = new(PaintFlags.AntiAlias);
    readonly Paint magFillPaint = new(PaintFlags.AntiAlias);
    readonly Paint magLinePaint = new(PaintFlags.AntiAlias);
    readonly Paint phaseLinePaint = new(PaintFlags.AntiAlias);
    readonly Paint nodeFillPaint = new(PaintFlags.AntiAlias);
    readonly Paint nodeHaloPaint = new(PaintFlags.AntiAlias);
    readonly Paint nodeStrokePaint = new(PaintFlags.AntiAlias);
    readonly Paint nodeTextPaint = new(PaintFlags.AntiAlias);
    readonly Android.Graphics.Path spectrumPath = new();
    readonly Android.Graphics.Path magPath = new();
    readonly Android.Graphics.Path magFillPath = new();
    readonly Android.Graphics.Path phasePath = new();
    readonly RectF plotRect = new();
    readonly RectF bgRect = new();
    readonly Java.Lang.Runnable spectrumTickRunnable;

    List<FilterStudioBandSpec> bands = [];
    int selectedBandIndex;
    int draggingBandIndex = -1;
    bool showPhaseOverlay = true;
    bool spectrumLoopActive;

    public event Action<int> BandSelected;
    public event Action<int, double, double> BandDragged;

    public int SelectedBandIndex {
        get => selectedBandIndex;
        set {
            selectedBandIndex = bands.Count > 0 ? Math.Clamp(value, 0, bands.Count - 1) : 0;
            Invalidate();
        }
    }

    public bool ShowPhaseOverlay {
        get => showPhaseOverlay;
        set {
            showPhaseOverlay = value;
            Invalidate();
        }
    }

    public FilterStudioGraphView(Context ctx, FilterStudioPostProcessingStage stage) : base(ctx) {
        this.stage = stage;
        dp = ctx.Resources?.DisplayMetrics?.Density ?? 3f;

        bgPaint.SetStyle(Paint.Style.Fill);
        bgPaint.Color = Color.Argb(180, 12, 13, 18);

        borderPaint.SetStyle(Paint.Style.Stroke);
        borderPaint.StrokeWidth = Math.Max(1f, dp * 1f);
        borderPaint.Color = OneUiTheme.GlassCardBorderTop;

        gridPaint.SetStyle(Paint.Style.Stroke);
        gridPaint.StrokeWidth = Math.Max(1f, dp * 0.75f);
        gridPaint.Color = Color.Argb(34, 255, 255, 255);

        zeroLinePaint.SetStyle(Paint.Style.Stroke);
        zeroLinePaint.StrokeWidth = Math.Max(1f, dp * 1.1f);
        zeroLinePaint.Color = Color.Argb(78, 255, 255, 255);

        labelPaint.Color = Color.Argb(165, 220, 224, 236);
        labelPaint.TextSize = 9.5f * dp;
        labelPaint.TextAlign = Paint.Align.Center;
        labelPaint.SetTypeface(OneUiTheme.GetAppleFontRegular(ctx));

        spectrumFillPaint.SetStyle(Paint.Style.Fill);
        spectrumFillPaint.Color = Color.Argb(46, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B);

        magFillPaint.SetStyle(Paint.Style.Fill);
        magFillPaint.Color = Color.Argb(36, OneUiTheme.Primary.R, OneUiTheme.Primary.G, OneUiTheme.Primary.B);

        magLinePaint.SetStyle(Paint.Style.Stroke);
        magLinePaint.StrokeWidth = dp * 2.2f;
        magLinePaint.StrokeCap = Paint.Cap.Round;
        magLinePaint.StrokeJoin = Paint.Join.Round;
        magLinePaint.Color = OneUiTheme.PrimaryHigh;

        phaseLinePaint.SetStyle(Paint.Style.Stroke);
        phaseLinePaint.StrokeWidth = dp * 1.3f;
        phaseLinePaint.Color = Color.Argb(145, OneUiTheme.FunctionalCyan.R, OneUiTheme.FunctionalCyan.G, OneUiTheme.FunctionalCyan.B);
        phaseLinePaint.SetPathEffect(new DashPathEffect([dp * 5f, dp * 4f], 0f));

        nodeFillPaint.SetStyle(Paint.Style.Fill);
        nodeHaloPaint.SetStyle(Paint.Style.Fill);
        nodeStrokePaint.SetStyle(Paint.Style.Stroke);
        nodeStrokePaint.StrokeWidth = dp * 1.8f;

        nodeTextPaint.Color = OneUiTheme.OnPrimary;
        nodeTextPaint.TextSize = 9.5f * dp;
        nodeTextPaint.TextAlign = Paint.Align.Center;
        nodeTextPaint.SetTypeface(OneUiTheme.GetAppleFontBold(ctx));

        spectrumTickRunnable = new Java.Lang.Runnable(() => {
            if (!spectrumLoopActive) {
                return;
            }
            Invalidate();
            PostDelayed(spectrumTickRunnable, 33);
        });
    }

    public void SetBands(List<FilterStudioBandSpec> newBands, int selectedIndex = -1) {
        bands = newBands ?? [];
        if (selectedIndex >= 0 && bands.Count > 0) {
            selectedBandIndex = Math.Clamp(selectedIndex, 0, bands.Count - 1);
        } else if (selectedBandIndex >= bands.Count) {
            selectedBandIndex = Math.Max(0, bands.Count - 1);
        }
        Invalidate();
    }

    protected override void OnAttachedToWindow() {
        base.OnAttachedToWindow();
        spectrumLoopActive = true;
        RemoveCallbacks(spectrumTickRunnable);
        PostDelayed(spectrumTickRunnable, 33);
    }

    protected override void OnDetachedFromWindow() {
        spectrumLoopActive = false;
        RemoveCallbacks(spectrumTickRunnable);
        base.OnDetachedFromWindow();
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        int w = Width;
        int h = Height;
        if (w <= 4 || h <= 4) {
            return;
        }

        float padL = 26f * dp;
        float padR = 10f * dp;
        float padT = 10f * dp;
        float padB = 20f * dp;
        bgRect.Set(1f, 1f, w - 1f, h - 1f);
        plotRect.Set(padL, padT, w - padR, h - padB);
        float corner = 18f * dp;

        canvas.DrawRoundRect(bgRect, corner, corner, bgPaint);
        canvas.DrawRoundRect(bgRect, corner, corner, borderPaint);

        // 1. Logarithmic frequency & dB grid
        for (int i = 0; i < GridFreqs.Length; i++) {
            float x = FreqToX(GridFreqs[i]);
            canvas.DrawLine(x, plotRect.Top, x, plotRect.Bottom, gridPaint);
            canvas.DrawText(GridFreqLabels[i], x, h - 6f * dp, labelPaint);
        }
        for (int i = 0; i < GridGains.Length; i++) {
            float y = GainToY(GridGains[i]);
            canvas.DrawLine(plotRect.Left, y, plotRect.Right, y, GridGains[i] == 0 ? zeroLinePaint : gridPaint);
            canvas.DrawText(GridGains[i] == 0 ? "0" : (GridGains[i] > 0 ? "+" + (int)GridGains[i] : ((int)GridGains[i]).ToString()),
                padL * 0.48f, y + 3.2f * dp, labelPaint);
        }

        // 2. Real-time 48-bin spectrum overlay
        if (stage != null) {
            stage.CopySpectrumDb(spectrumBuf);
            spectrumPath.Reset();
            int n = spectrumBuf.Length;
            float baseLine = plotRect.Bottom;
            spectrumPath.MoveTo(plotRect.Left, baseLine);
            for (int i = 0; i < n; i++) {
                float t = i / (float)(n - 1);
                float x = plotRect.Left + t * plotRect.Width();
                float norm = Math.Clamp((spectrumBuf[i] + 72f) / 72f, 0f, 1f);
                float y = baseLine - norm * plotRect.Height() * 0.92f;
                spectrumPath.LineTo(x, y);
            }
            spectrumPath.LineTo(plotRect.Right, baseLine);
            spectrumPath.Close();
            canvas.DrawPath(spectrumPath, spectrumFillPaint);
        }

        // 3. Composite magnitude & optional phase response curves
        if (stage != null) {
            magPath.Reset();
            magFillPath.Reset();
            phasePath.Reset();
            float zeroY = GainToY(0.0);
            for (int i = 0; i < CurveSamples; i++) {
                float t = i / (float)(CurveSamples - 1);
                double freq = Math.Pow(10.0, LogMin + t * LogSpan);
                stage.EvaluateCompositeResponse(freq, out double magDb, out double phaseRad);
                float x = plotRect.Left + t * plotRect.Width();
                float magY = GainToY(Math.Clamp(magDb, MinGainDb, MaxGainDb));
                float phaseY = PhaseToY(phaseRad);
                if (i == 0) {
                    magPath.MoveTo(x, magY);
                    magFillPath.MoveTo(x, zeroY);
                    magFillPath.LineTo(x, magY);
                    phasePath.MoveTo(x, phaseY);
                } else {
                    magPath.LineTo(x, magY);
                    magFillPath.LineTo(x, magY);
                    phasePath.LineTo(x, phaseY);
                }
            }
            magFillPath.LineTo(plotRect.Right, zeroY);
            magFillPath.Close();

            canvas.DrawPath(magFillPath, magFillPaint);
            if (showPhaseOverlay) {
                canvas.DrawPath(phasePath, phaseLinePaint);
            }
            canvas.DrawPath(magPath, magLinePaint);
        }

        // 4. Interactive draggable band nodes
        for (int i = 0; i < bands.Count; i++) {
            FilterStudioBandSpec b = bands[i];
            if (b == null) {
                continue;
            }
            float nx = FreqToX(b.FrequencyHz);
            float ny = GainToY(UsesGain(b.Type) ? b.GainDb : 0.0);
            bool isSel = i == selectedBandIndex;
            float r = (isSel ? 11.5f : 8.5f) * dp;

            if (isSel) {
                nodeHaloPaint.Color = Color.Argb(72, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B);
                canvas.DrawCircle(nx, ny, r + 6f * dp, nodeHaloPaint);
            }
            nodeFillPaint.Color = b.Enabled
                ? (isSel ? OneUiTheme.Primary : Color.Argb(220, OneUiTheme.IconStudioExport.R, OneUiTheme.IconStudioExport.G, OneUiTheme.IconStudioExport.B))
                : Color.Argb(130, 110, 114, 128);
            nodeStrokePaint.Color = isSel ? Color.White : Color.Argb(180, 255, 255, 255);
            canvas.DrawCircle(nx, ny, r, nodeFillPaint);
            canvas.DrawCircle(nx, ny, r, nodeStrokePaint);
            canvas.DrawText((i + 1).ToString(), nx, ny + 3.4f * dp, nodeTextPaint);
        }
    }

    public override bool OnTouchEvent(MotionEvent e) {
        if (e == null || bands.Count == 0 || plotRect.Width() <= 4f) {
            return false;
        }
        float tx = e.GetX();
        float ty = e.GetY();
        switch (e.ActionMasked) {
            case MotionEventActions.Down: {
                int nearest = -1;
                float bestDist = 34f * dp;
                for (int i = 0; i < bands.Count; i++) {
                    FilterStudioBandSpec b = bands[i];
                    if (b == null) {
                        continue;
                    }
                    float nx = FreqToX(b.FrequencyHz);
                    float ny = GainToY(UsesGain(b.Type) ? b.GainDb : 0.0);
                    float dx = tx - nx;
                    float dy = ty - ny;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    if (d < bestDist) {
                        bestDist = d;
                        nearest = i;
                    }
                }
                if (nearest >= 0) {
                    draggingBandIndex = nearest;
                    selectedBandIndex = nearest;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    BandSelected?.Invoke(nearest);
                    Invalidate();
                    return true;
                }
                return false;
            }
            case MotionEventActions.Move:
                if (draggingBandIndex >= 0 && draggingBandIndex < bands.Count) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    double newFreq = XToFreq(tx);
                    double newGain = UsesGain(bands[draggingBandIndex].Type) ? YToGain(ty) : 0.0;
                    bands[draggingBandIndex].FrequencyHz = newFreq;
                    bands[draggingBandIndex].GainDb = newGain;
                    BandDragged?.Invoke(draggingBandIndex, newFreq, newGain);
                    Invalidate();
                    return true;
                }
                return false;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                draggingBandIndex = -1;
                Parent?.RequestDisallowInterceptTouchEvent(false);
                return true;
        }
        return base.OnTouchEvent(e);
    }

    static bool UsesGain(CytomicSvfType t) =>
        t == CytomicSvfType.Peak || t == CytomicSvfType.LowShelf || t == CytomicSvfType.HighShelf;

    float FreqToX(double freqHz) {
        double clamped = Math.Clamp(freqHz, MinFreqHz, MaxFreqHz);
        double norm = (Math.Log10(clamped) - LogMin) / LogSpan;
        return plotRect.Left + (float)(norm * plotRect.Width());
    }

    double XToFreq(float x) {
        double norm = Math.Clamp((x - plotRect.Left) / Math.Max(1f, plotRect.Width()), 0.0, 1.0);
        return Math.Clamp(Math.Pow(10.0, LogMin + norm * LogSpan), MinFreqHz, MaxFreqHz);
    }

    float GainToY(double gainDb) {
        double norm = (Math.Clamp(gainDb, MinGainDb, MaxGainDb) - MinGainDb) / (MaxGainDb - MinGainDb);
        return plotRect.Bottom - (float)(norm * plotRect.Height());
    }

    double YToGain(float y) {
        double norm = Math.Clamp((plotRect.Bottom - y) / Math.Max(1f, plotRect.Height()), 0.0, 1.0);
        return Math.Round((MinGainDb + norm * (MaxGainDb - MinGainDb)) * 4.0) * 0.25;
    }

    float PhaseToY(double phaseRad) {
        double norm = (Math.Clamp(phaseRad, -Math.PI, Math.PI) + Math.PI) / (2.0 * Math.PI);
        return plotRect.Bottom - (float)(norm * plotRect.Height());
    }
}
