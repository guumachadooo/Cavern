package hu.sbence.cavernize.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import hu.sbence.cavernize.R

val SpaceGroteskFamily = FontFamily(
    Font(R.font.space_grotesk, FontWeight.Normal),
    Font(R.font.space_grotesk, FontWeight.SemiBold),
    Font(R.font.space_grotesk, FontWeight.Bold)
)

val PlusJakartaSansFamily = FontFamily(
    Font(R.font.plus_jakarta_sans, FontWeight.Normal),
    Font(R.font.plus_jakarta_sans, FontWeight.Medium),
    Font(R.font.plus_jakarta_sans, FontWeight.SemiBold)
)

val JetBrainsMonoFamily = FontFamily(
    Font(R.font.jetbrains_mono, FontWeight.Normal),
    Font(R.font.jetbrains_mono, FontWeight.Medium),
    Font(R.font.jetbrains_mono, FontWeight.Bold)
)

val StudioObsidian = Color(0xFF101216)
val StudioSurface = Color(0xFF1A1D24)
val StudioCard = Color(0xFF222630)
val StudioBorder = Color(0xFF323846)
val CavernCyan = Color(0xFF38BDF8)
val CavernCyanDim = Color(0xFF0284C7)
val SpeakerActiveEmerald = Color(0xFF22C55E)
val AtmosDynamicAmber = Color(0xFFF59E0B)
val SpeakerInactiveSlate = Color(0xFF475569)
val AlertRed = Color(0xFFEF4444)

private val CavernDarkColorScheme = darkColorScheme(
    primary = CavernCyan,
    onPrimary = Color(0xFF06131C),
    primaryContainer = Color(0xFF0C2D42),
    onPrimaryContainer = Color(0xFFBAE6FD),
    secondary = SpeakerActiveEmerald,
    onSecondary = Color(0xFF052E16),
    secondaryContainer = Color(0xFF143824),
    onSecondaryContainer = Color(0xFFBBF7D0),
    tertiary = AtmosDynamicAmber,
    onTertiary = Color(0xFF291602),
    tertiaryContainer = Color(0xFF3D2608),
    onTertiaryContainer = Color(0xFFFDE68A),
    background = StudioObsidian,
    onBackground = Color(0xFFF1F5F9),
    surface = StudioSurface,
    onSurface = Color(0xFFF1F5F9),
    surfaceVariant = StudioCard,
    onSurfaceVariant = Color(0xFF94A3B8),
    outline = StudioBorder,
    error = AlertRed
)

val CavernTypography = Typography(
    headlineMedium = TextStyle(
        fontFamily = SpaceGroteskFamily,
        fontWeight = FontWeight.Bold,
        fontSize = 24.sp,
        lineHeight = 30.sp
    ),
    titleLarge = TextStyle(
        fontFamily = SpaceGroteskFamily,
        fontWeight = FontWeight.Bold,
        fontSize = 19.sp,
        lineHeight = 25.sp
    ),
    titleMedium = TextStyle(
        fontFamily = SpaceGroteskFamily,
        fontWeight = FontWeight.SemiBold,
        fontSize = 16.sp,
        lineHeight = 22.sp
    ),
    titleSmall = TextStyle(
        fontFamily = SpaceGroteskFamily,
        fontWeight = FontWeight.SemiBold,
        fontSize = 14.sp,
        lineHeight = 19.sp
    ),
    bodyLarge = TextStyle(
        fontFamily = PlusJakartaSansFamily,
        fontWeight = FontWeight.Normal,
        fontSize = 15.sp,
        lineHeight = 22.sp
    ),
    bodyMedium = TextStyle(
        fontFamily = PlusJakartaSansFamily,
        fontWeight = FontWeight.Normal,
        fontSize = 13.sp,
        lineHeight = 19.sp
    ),
    bodySmall = TextStyle(
        fontFamily = PlusJakartaSansFamily,
        fontWeight = FontWeight.Normal,
        fontSize = 12.sp,
        lineHeight = 16.sp
    ),
    labelLarge = TextStyle(
        fontFamily = SpaceGroteskFamily,
        fontWeight = FontWeight.SemiBold,
        fontSize = 13.sp,
        lineHeight = 18.sp
    ),
    labelMedium = TextStyle(
        fontFamily = JetBrainsMonoFamily,
        fontWeight = FontWeight.Medium,
        fontSize = 11.sp,
        lineHeight = 15.sp
    )
)

@Composable
fun CavernizeTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = CavernDarkColorScheme,
        typography = CavernTypography,
        content = content
    )
}
