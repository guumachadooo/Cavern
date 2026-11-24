namespace CavernizeAndroid.Language;

public sealed partial class AndroidStrings {
    static void ApplyChinese(Dictionary<string, string> d) {
        d["OpenFile"] = "打开文件";
        d["TrackLabel"] = "音轨:";
        d["TabMusicPlayer"] = "播放器";
        d["TabPlayer"] = "3D 房间";
        d["TabConvert"] = "转换导出";
        d["TabExtract"] = "分轨提取";
        d["TabSettings"] = "设置";
        d["ModeCover"] = "封面";
        d["ModeVideo"] = "视频";
        d["ModeLyrics"] = "歌词";
        d["Play"] = "播放";
        d["Pause"] = "暂停";
        d["Stop"] = "停止";
        d["RenderBtn"] = "开始渲染";
        d["AddQueueBtn"] = "添加到队列";
        d["ShowMetaBtn"] = "编码器元数据";
        d["ShowReportBtn"] = "渲染后报告";
        d["WrGui"] = "接线指南";
        d["ErrorTitle"] = "错误";
        d["UpdateCardTitle"] = "软件更新";
        d["CheckUpdateBtn"] = "检查更新";
        d["DownloadInstallBtn"] = "下载并安装";
        d["LockRoom"] = "自由移动位置";
        d["UnlockRoom"] = "位置已锁定";
        d["FineTuneBtn"] = "微调位置与头部朝向";
        d["FineTuneHideBtn"] = "隐藏微调滑块";

        // Wave 2B Studio 压缩档位、后台导出与 3D 对象编排
        d["StudioCompressionTierLabel"] = "压缩与音质";
        d["TipStudioCompressionTier"] = "选择空间母带导出的编码打包方式：\n\n• 母带 PCM：无压缩工作室级 PCM WAV、IAMF 或 AMB。\n• 无损压缩：体积更小的逐位无损 FLAC / 无损容器（无需配对 LGSR 无损孪生文件）。\n• 便携有损：紧凑的 Opus / E-AC-3，适合手机分享。";
        d["StudioTierMasterPcm"] = "母带 PCM";
        d["StudioTierLossless"] = "无损压缩";
        d["StudioTierPortable"] = "便携有损";
        d["StudioCodecDetailsToggleFmt"] = "编码详情 · {0} ({1})";
        d["StudioCodecDetailsBodyFmt"] = "容器: {0} · 规格: {1} · 峰值上限: {2:0.0} dBTP";
        d["StudioAtmosTierLockWarn"] = "Dolby Atmos ADM BWF 需要母带 PCM 以保留动态 3D 对象元数据。请切换至 Eclipsa IAMF 以使用无损或便携压缩。";
        d["StudioSwitchToEclipsaBtn"] = "切换为 Eclipsa IAMF";
        d["StudioExportLowSpaceError"] = "存储可用空间不足，无法完成此次导出。";
        d["StudioExportFgCancelledToast"] = "导出已取消。";
        d["SpatialChoreoLabel"] = "3D 对象动态编排:";
        d["TipSpatialChoreo"] = "结合音频特征与歌词情绪，在 3D 房间中平滑编排分离后的乐器对象位置（仅移动 3D 对象 — 不施加任何声乐效果器）。\n\n• 关闭：静态 ITU-R BS.2127 锚点。\n• 柔和：轻微前场与天空声道呼吸感。\n• 音乐：随节拍与段落变化的空间舞台。\n• 影院：宽广环绕与天空弧线轨迹。\n• 环绕轨道：平滑球面环绕运动。";
        d["SpatialChoreoOff"] = "关闭（静态）";
        d["SpatialChoreoSubtle"] = "柔和";
        d["SpatialChoreoMusical"] = "音乐";
        d["SpatialChoreoCinematic"] = "影院";
        d["SpatialChoreoOrbit"] = "环绕轨道";
    }
}
