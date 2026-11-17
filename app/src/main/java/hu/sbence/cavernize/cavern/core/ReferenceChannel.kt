package hu.sbence.cavernize.cavern.core

import kotlin.math.cos
import kotlin.math.sin

data class Vector3(val x: Float, val y: Float, val z: Float) {
    operator fun plus(other: Vector3) = Vector3(x + other.x, y + other.y, z + other.z)
    operator fun minus(other: Vector3) = Vector3(x - other.x, y - other.y, z - other.z)
    operator fun times(scalar: Float) = Vector3(x * scalar, y * scalar, z * scalar)
    operator fun div(scalar: Float) = if (scalar != 0f) Vector3(x / scalar, y / scalar, z / scalar) else Vector3(0f, 0f, 0f)

    fun distanceSquaredTo(other: Vector3): Float {
        val dx = x - other.x
        val dy = y - other.y
        val dz = z - other.z
        return dx * dx + dy * dy + dz * dz
    }
}

/**
 * All 29 reference channels defined in Cavern.Channels.ReferenceChannel.
 */
enum class ReferenceChannel(
    val shortMark: String,
    val dciMark: String,
    val waveMaskBit: Int
) {
    FrontLeft("FL", "L", 0x1),
    FrontRight("FR", "R", 0x2),
    FrontCenter("FC", "C", 0x4),
    ScreenLFE("LFE", "LFE", 0x8),
    RearLeft("RL", "Lrs", 0x10),
    RearRight("RR", "Rrs", 0x20),
    SideLeft("SL", "Lss", 0x200),
    SideRight("SR", "Rss", 0x400),
    FrontLeftCenter("FLC", "Lc", 0x40),
    FrontRightCenter("FRC", "Rc", 0x80),
    HearingImpaired("HI", "HI", 0),
    VisuallyImpaired("VI", "VI", 0),
    Unknown("UNK", "UNK", 0),
    MotionData("MD", "MD", 0),
    ExternalData("ES", "ES", 0),
    TopFrontLeft("TFL", "Ltf", 0x1000),
    TopFrontRight("TFR", "Rtf", 0x4000),
    TopSideLeft("TSL", "Lts", 0),
    TopSideRight("TSR", "Rts", 0),
    SignLanguage("SLV", "SLV", 0),
    BottomSurround("BS", "Bs", 0),
    TopFrontCenter("TFC", "Ctf", 0x2000),
    GodsVoice("GV", "VoG", 0x800),
    RearCenter("RC", "Cs", 0x100),
    WideLeft("WL", "Lw", 0),
    WideRight("WR", "Rw", 0),
    TopRearLeft("TRL", "Ltr", 0x8000),
    TopRearRight("TRR", "Rtr", 0x20000),
    TopRearCenter("TRC", "Ctr", 0x10000);

    val isHeightChannel: Boolean
        get() = this == TopFrontLeft || this == TopFrontRight || this == TopSideLeft ||
            this == TopSideRight || this == TopFrontCenter || this == GodsVoice ||
            this == TopRearLeft || this == TopRearRight || this == TopRearCenter

    val isLfe: Boolean
        get() = this == ScreenLFE
}

/**
 * Exact port of Cavern.Channels.ChannelPrototype.
 * Angles: y = horizontal azimuth in degrees (- left, + right), x = vertical elevation in degrees (- overhead, + bottom).
 */
data class ChannelPrototype(
    val y: Float,
    val x: Float = 0f,
    val name: String,
    val lfe: Boolean = false,
    val muted: Boolean = false
) {
    /**
     * Converts spherical coordinates (Y=azimuth, X=elevation) to Cavern's 3D unit cube position [-1..1].
     */
    fun toPositionVector(): Vector3 {
        if (lfe) return Vector3(-1f, -1f, 1f)
        val radY = Math.toRadians(y.toDouble())
        val radX = Math.toRadians(x.toDouble())
        val cosX = cos(radX).toFloat()
        val px = (sin(radY) * cosX).toFloat().coerceIn(-1f, 1f)
        val py = (-sin(radX)).toFloat().coerceIn(-1f, 1f)
        val pz = (cos(radY) * cosX).toFloat().coerceIn(-1f, 1f)
        return Vector3(px, py, pz)
    }

    companion object {
        val FrontLeft = ChannelPrototype(-30f, 0f, "Front left")
        val FrontRight = ChannelPrototype(30f, 0f, "Front right")
        val FrontCenter = ChannelPrototype(0f, 0f, "Front center")
        val ScreenLFE = ChannelPrototype(0f, 0f, "Low frequency effects", lfe = true)
        val RearLeft = ChannelPrototype(-150f, 0f, "Rear left")
        val RearRight = ChannelPrototype(150f, 0f, "Rear right")
        val SideLeft = ChannelPrototype(-110f, 0f, "Side left")
        val SideRight = ChannelPrototype(110f, 0f, "Side right")
        val FrontLeftCenter = ChannelPrototype(-15f, 0f, "Front left center")
        val FrontRightCenter = ChannelPrototype(15f, 0f, "Front right center")
        val HearingImpaired = ChannelPrototype(0f, 0f, "Hearing impaired", muted = true)
        val VisuallyImpaired = ChannelPrototype(0f, 0f, "Visually impaired narrative", muted = true)
        val Unused = ChannelPrototype(0f, 0f, "Unused", muted = true)
        val MotionData = ChannelPrototype(0f, 0f, "Motion data sync", muted = true)
        val ExternalData = ChannelPrototype(0f, 0f, "External sync signal", muted = true)
        val TopFrontLeft = ChannelPrototype(-70f, -45f, "Top front left")
        val TopFrontRight = ChannelPrototype(70f, -45f, "Top front right")
        val TopSideLeft = ChannelPrototype(-110f, -45f, "Top side left")
        val TopSideRight = ChannelPrototype(110f, -45f, "Top side right")
        val SignLanguage = ChannelPrototype(0f, 0f, "Sign language video", muted = true)
        val BottomSurround = ChannelPrototype(0f, 90f, "Bottom surround")
        val TopFrontCenter = ChannelPrototype(0f, -45f, "Top front center")
        val GodsVoice = ChannelPrototype(0f, -90f, "God's voice")
        val RearCenter = ChannelPrototype(180f, 0f, "Rear center")
        val WideLeft = ChannelPrototype(-70f, 0f, "Wide left")
        val WideRight = ChannelPrototype(70f, 0f, "Wide right")
        val TopRearLeft = ChannelPrototype(-150f, -45f, "Top rear left")
        val TopRearRight = ChannelPrototype(150f, -45f, "Top rear right")
        val TopRearCenter = ChannelPrototype(180f, -45f, "Top rear center")

        val AlternativePositions: Array<Vector3> = arrayOf(
            Vector3(-1f, 0f, 1f),       // FrontLeft
            Vector3(1f, 0f, 1f),        // FrontRight
            Vector3(0f, 0f, 1f),        // FrontCenter
            Vector3(-1f, -1f, 1f),      // ScreenLFE
            Vector3(-1f, 0f, -1f),      // RearLeft
            Vector3(1f, 0f, -1f),       // RearRight
            Vector3(-1f, 0f, 0f),       // SideLeft
            Vector3(1f, 0f, 0f),        // SideRight
            Vector3(-0.5f, 0f, 1f),     // FrontLeftCenter
            Vector3(0.5f, 0f, 1f),      // FrontRightCenter
            Vector3(0f, 0f, 1f),        // HearingImpaired
            Vector3(0f, 0f, 1f),        // VisuallyImpaired
            Vector3(0f, 0f, 1f),        // Unknown
            Vector3(0f, 0f, 1f),        // MotionData
            Vector3(0f, 0f, 1f),        // ExternalData
            Vector3(-1f, 1f, 1f),       // TopFrontLeft
            Vector3(1f, 1f, 1f),        // TopFrontRight
            Vector3(-1f, 1f, 0f),       // TopSideLeft
            Vector3(1f, 1f, 0f),        // TopSideRight
            Vector3(0f, 0f, 1f),        // SignLanguage
            Vector3(0f, -1f, 0f),       // BottomSurround
            Vector3(0f, 1f, 1f),        // TopFrontCenter
            Vector3(0f, 1f, 0f),        // GodsVoice
            Vector3(0f, 0f, -1f),       // RearCenter
            Vector3(-1f, 0f, 0.677419f),// WideLeft
            Vector3(1f, 0f, 0.677419f), // WideRight
            Vector3(-1f, 1f, -1f),      // TopRearLeft
            Vector3(1f, 1f, -1f),       // TopRearRight
            Vector3(0f, 1f, -1f)        // TopRearCenter
        )

        val Mapping: Array<ChannelPrototype> = arrayOf(
            FrontLeft, FrontRight, FrontCenter, ScreenLFE, RearLeft, RearRight, SideLeft, SideRight,
            FrontLeftCenter, FrontRightCenter, HearingImpaired, VisuallyImpaired, Unused, MotionData, ExternalData,
            TopFrontLeft, TopFrontRight, TopSideLeft, TopSideRight, SignLanguage, BottomSurround, TopFrontCenter,
            GodsVoice, RearCenter, WideLeft, WideRight, TopRearLeft, TopRearRight, TopRearCenter
        )

        fun fromReference(channel: ReferenceChannel): ChannelPrototype = Mapping[channel.ordinal]
        fun cubePosition(channel: ReferenceChannel): Vector3 = AlternativePositions[channel.ordinal]

        val ref100 = arrayOf(ReferenceChannel.FrontCenter)
        val ref200 = arrayOf(ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight)
        val ref300 = arrayOf(ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter)
        val ref312 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight
        )
        val ref400 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight
        )
        val ref404 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight,
            ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
            ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
        )
        val ref411 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.RearCenter, ReferenceChannel.TopFrontCenter
        )
        val ref413 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.RearCenter, ReferenceChannel.TopFrontCenter,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
        )
        val ref500 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight
        )
        val ref510 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.SideLeft, ReferenceChannel.SideRight
        )
        val ref512 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.TopSideLeft, ReferenceChannel.TopSideRight
        )
        val ref514 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
        )
        val ref516 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
            ReferenceChannel.TopSideLeft, ReferenceChannel.TopSideRight,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
        )
        val wav516 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontCenter, ReferenceChannel.TopFrontRight,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearCenter, ReferenceChannel.TopRearRight
        )
        val ref710 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight
        )
        val ref712 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight
        )
        val ref714 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
        )
        val ref716 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
            ReferenceChannel.TopSideLeft, ReferenceChannel.TopSideRight,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
        )
        val wav716 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontCenter, ReferenceChannel.TopFrontRight,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearCenter, ReferenceChannel.TopRearRight
        )
        val ref910 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.WideLeft, ReferenceChannel.WideRight
        )
        val ref914 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.WideLeft, ReferenceChannel.WideRight,
            ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
        )
        val ref916 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.WideLeft, ReferenceChannel.WideRight,
            ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
            ReferenceChannel.TopSideLeft, ReferenceChannel.TopSideRight,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
        )
        val wav916 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.WideLeft, ReferenceChannel.WideRight,
            ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontCenter, ReferenceChannel.TopFrontRight,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearCenter, ReferenceChannel.TopRearRight
        )

        fun getStandardMatrix(count: Int): Array<ReferenceChannel> = when (count) {
            1 -> ref100
            2 -> ref200
            3 -> ref300
            4 -> ref400
            5 -> ref500
            6 -> ref510
            7 -> arrayOf(
                ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
                ReferenceChannel.RearLeft, ReferenceChannel.RearRight, ReferenceChannel.SideLeft, ReferenceChannel.SideRight
            )
            8 -> ref710
            10 -> ref712
            12 -> ref714
            14 -> wav716
            16 -> wav916
            else -> Array(count) { idx -> ReferenceChannel.entries[idx % ReferenceChannel.entries.size] }
        }
    }
}
