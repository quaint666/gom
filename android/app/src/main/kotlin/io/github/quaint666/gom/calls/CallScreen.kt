package io.github.quaint666.gom.calls

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.drawscope.rotate
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.res.colorResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.quaint666.gom.R
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlinx.coroutines.delay

/** The page's colours (`gom.css`), light or dark as the phone is; `CallColorsTest` keeps them the same. */
private class Palette(
    val bg: Color, val surface: Color, val text: Color, val muted: Color, val border: Color, val tint: Color, val link: Color,
    val btn: Color, val hang: Color, val warnBg: Color, val warnText: Color, val warnBorder: Color,
)

@Composable
private fun palette(): Palette = Palette(colorResource(R.color.page), colorResource(R.color.call_surface), colorResource(R.color.call_text),
    colorResource(R.color.call_muted), colorResource(R.color.call_border), colorResource(R.color.call_tint), colorResource(R.color.call_link),
    colorResource(R.color.gom), colorResource(R.color.call_hang), colorResource(R.color.call_warn_bg), colorResource(R.color.call_warn_text),
    colorResource(R.color.call_warn_border))

/** The page's icons (`Scripts/icons.ts`, and the mockups' for the sound routes): 24-unit strokes. */
private object Icons {
    val logo = listOf("M4 5h16v11H11l-4 4v-4H4z", "M8 10h8")
    val call = listOf("M5 4h4l2 5-2.5 1.5a11 11 0 005 5L15 13l5 2v4a2 2 0 01-2 2A16 16 0 013 6a2 2 0 012-2z")
    val mic = listOf("M12 3a3 3 0 00-3 3v6a3 3 0 006 0V6a3 3 0 00-3-3z", "M5 11a7 7 0 0014 0", "M12 18v3")
    val micOff = mic + "M3 3l18 18"
    val chevronDown = listOf("M6 9l6 6 6-6")
    val wifiOff = listOf("M2 8.8a15 15 0 0120 0", "M5 12.5a10 10 0 0114 0", "M8.5 16a5 5 0 017 0", "M12 19.5h.01", "M3 3l18 18")
    val check = listOf("M5 12.5l4.5 4.5L19 7.5")
    val close = listOf("M6 6l12 12", "M18 6L6 18")
    val speaker = listOf("M4 9h4l5-4v14l-5-4H4z", "M16.5 8.5a5 5 0 010 7M19 6a8.5 8.5 0 010 12")
    val bluetooth = listOf("M7 7l10 10-5 4V3l5 4L7 17")
    val phone = listOf("M8 3h8a1 1 0 011 1v16a1 1 0 01-1 1H8a1 1 0 01-1-1V4a1 1 0 011-1z", "M11 18h2")
    val headset = listOf("M4 15v-3a8 8 0 0116 0v3", "M4 15h3v5H4z", "M17 15h3v5h-3z")
    val lock = listOf("M6 11h12v9H6z", "M8.5 11V8a3.5 3.5 0 017 0v3")
}

@Composable
private fun Icon(paths: List<String>, tint: Color, size: Dp = 24.dp, rotate: Boolean = false) {
    val parsed = remember(paths) { paths.map { PathParser().parsePathString(it).toPath() } }
    Canvas(Modifier.size(size)) {
        val k = this.size.minDimension / 24f
        scale(k, pivot = Offset.Zero) {
            // The end button's handset lies down, as the page turns it (`.rb.hang .i`).
            rotate(if (rotate) 135f else 0f, pivot = Offset(12f, 12f)) {
                for (p in parsed) drawPath(p, tint, style = Stroke(width = 1.8f, cap = StrokeCap.Round, join = StrokeJoin.Round))
            }
        }
    }
}

private fun routeIcon(kind: RouteKind?) = when (kind) {
    RouteKind.BLUETOOTH -> Icons.bluetooth
    RouteKind.HEADSET -> Icons.headset
    RouteKind.EARPIECE -> Icons.phone
    else -> Icons.speaker
}

/**
 * A call, full screen (mockups 21 to 28): who, what is happening, and one row of buttons. [locked]: shown over the lock screen, where
 * [hiddenTitle] stands in for the name when the person's notification level names nobody (decision 8, mockup 22).
 */
@Composable
fun CallScreen(
    view: CallView?,
    routes: Routes,
    locked: Boolean,
    hiddenTitle: String?,
    act: CallActions,
    chooseRoute: Boolean,
    onChooseRoute: (Boolean) -> Unit,
) {
    val c = palette()
    val dark = isSystemInDarkTheme()
    val v = view
    val ringing = v == null || v.phase == Phase.INCOMING
    val overLock = locked && ringing
    // Nobody named on the lock screen at the "nothing" level (mockup 22), or before the server has said who calls.
    val anonymous = overLock && (hiddenTitle != null || v == null)
    val name = if (anonymous) stringResource(R.string.call_incoming) else v?.peer?.name.orEmpty()
    Box(Modifier.fillMaxSize().background(Brush.verticalGradient(0f to c.tint, 0.62f to c.bg))) {
        Column(Modifier.fillMaxSize().safeDrawingPadding().padding(horizontal = 24.dp, vertical = 12.dp), horizontalAlignment = Alignment.CenterHorizontally) {
            if (overLock) Clock(c)
            else Header(c, showMinimise = v != null && v.phase in setOf(Phase.OUTGOING, Phase.CONNECTING, Phase.ACTIVE), act.minimise)
            Spacer(Modifier.height(if (overLock) 28.dp else 40.dp))
            if (anonymous) Anonymous(c) else Avatar(name, c)
            Spacer(Modifier.height(18.dp))
            BasicText(name, style = TextStyle(color = c.text, fontSize = 28.sp, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center))
            Spacer(Modifier.height(12.dp))
            if (anonymous) Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.lock, c.muted, 18.dp)
                Spacer(Modifier.width(8.dp))
                BasicText(stringResource(R.string.call_unlock_to_see), style = TextStyle(color = c.muted, fontSize = 16.sp))
            } else Status(v, c)
            Spacer(Modifier.weight(1f))
            if (v?.downSince != null) {
                BasicText(stringResource(R.string.call_reconnect_note), Modifier.widthIn(max = 320.dp).padding(bottom = 20.dp),
                    style = TextStyle(color = c.muted, fontSize = 14.sp, textAlign = TextAlign.Center))
            }
            Controls(v, routes, c, act, onChooseRoute)
        }
        if (chooseRoute && v != null) RouteSheet(routes, c, dark, onPick = { act.route(it); onChooseRoute(false) }, onDismiss = { onChooseRoute(false) })
    }
}

/** What the screen's buttons do: each tells the call service, or leaves the screen. */
class CallActions(
    val answer: () -> Unit,
    val hangUp: () -> Unit,
    val mute: (Boolean) -> Unit,
    val route: (Route) -> Unit,
    val minimise: () -> Unit,
    val close: () -> Unit,
    val callBack: () -> Unit,
    val message: () -> Unit,
)

@Composable
private fun Header(c: Palette, showMinimise: Boolean, minimise: () -> Unit) {
    Box(Modifier.fillMaxWidth().height(48.dp)) {
        if (showMinimise) {
            val label = stringResource(R.string.call_minimise)
            Box(Modifier.align(Alignment.CenterStart).size(48.dp).clip(CircleShape)
                .clickable(role = Role.Button, onClick = minimise).semantics { contentDescription = label }, contentAlignment = Alignment.Center) {
                Icon(Icons.chevronDown, c.text)
            }
        }
        Row(Modifier.align(Alignment.Center), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.logo, c.link, 16.dp)
            Spacer(Modifier.width(6.dp))
            BasicText(stringResource(R.string.call_voice), style = TextStyle(color = c.muted, fontSize = 13.sp))
        }
    }
}

/** The time over the lock screen, as Android's own ringing screen shows it (mockup 23). */
@Composable
private fun Clock(c: Palette) {
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(Unit) { while (true) { delay(1_000); now = System.currentTimeMillis() } }
    val at = Instant.ofEpochMilli(now).atZone(ZoneId.systemDefault())
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        BasicText(at.format(DateTimeFormatter.ofPattern("HH:mm")), style = TextStyle(color = c.text, fontSize = 64.sp, fontWeight = FontWeight.Medium))
        BasicText(at.format(DateTimeFormatter.ofPattern("EEEE, d MMMM")).replaceFirstChar { it.uppercase() },
            style = TextStyle(color = c.muted, fontSize = 15.sp))
    }
}

/** A handset in a plain circle, where the caller's picture would name them. */
@Composable
private fun Anonymous(c: Palette) {
    Box(Modifier.size(120.dp).clip(CircleShape).background(c.surface).border(1.dp, c.border, CircleShape), contentAlignment = Alignment.Center) {
        Icon(Icons.call, c.muted, 44.dp)
    }
}

@Composable
private fun Avatar(name: String, c: Palette) {
    Box(Modifier.size(120.dp).clip(CircleShape).background(c.tint), contentAlignment = Alignment.Center) {
        BasicText(name.trim().firstOrNull()?.uppercase() ?: "", style = TextStyle(color = c.link, fontSize = 42.sp, fontWeight = FontWeight.Bold))
    }
}

@Composable
private fun Status(v: CallView?, c: Palette) {
    if (v?.downSince != null) {
        Row(Modifier.clip(RoundedCornerShape(999.dp)).background(c.warnBg).border(1.dp, c.warnBorder, RoundedCornerShape(999.dp))
            .padding(horizontal = 12.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.wifiOff, c.warnText, 16.dp)
            Spacer(Modifier.width(8.dp))
            BasicText(stringResource(R.string.call_reconnecting), style = TextStyle(color = c.warnText, fontSize = 14.sp))
        }
        return
    }
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    if (v?.phase == Phase.ACTIVE) LaunchedEffect(v.connectedAt) { while (true) { now = System.currentTimeMillis(); delay(500) } }
    val text = when {
        v == null || v.phase == Phase.INCOMING -> stringResource(R.string.call_voice)
        v.phase == Phase.ENDED -> endingText(v.ending)
        v.phase == Phase.ACTIVE && v.connectedAt != null -> callLength(maxOf(0, now - v.connectedAt) / 1000)
        v.phase == Phase.CONNECTING -> stringResource(R.string.call_connecting)
        v.ringing -> stringResource(R.string.call_ringing)
        else -> stringResource(R.string.call_calling)
    }
    val problem = v?.ending?.problem == true
    BasicText(text, Modifier.widthIn(max = 320.dp).semantics { if (problem) stateDescription = text },
        style = TextStyle(color = if (problem) c.hang else c.muted, fontSize = 16.sp, textAlign = TextAlign.Center))
}

@Composable
private fun endingText(e: Ending?): String = when (e) {
    is Ending.Talked -> stringResource(R.string.call_ended_after, callLength(e.seconds))
    Ending.NoAnswer -> stringResource(R.string.call_no_answer)
    Ending.DeclinedByThem -> stringResource(R.string.call_declined_by_them)
    Ending.Busy -> stringResource(R.string.call_busy)
    Ending.AnsweredElsewhere -> stringResource(R.string.call_answered_elsewhere)
    Ending.Failed -> stringResource(R.string.call_failed)
    Ending.NeedMic -> stringResource(R.string.call_need_mic)
    is Ending.Refused -> stringResource(when (e.error) {
        "cannot_take_calls" -> R.string.call_error_cannot_take_calls
        "in_call" -> R.string.call_error_in_call
        "call_waiting" -> R.string.call_error_call_waiting
        "too_many_calls" -> R.string.call_error_too_many_calls
        "invalid_call", "not_friends" -> R.string.call_error_invalid_call
        "unsupported_version" -> R.string.call_error_unsupported_version
        null -> R.string.call_error_network
        else -> R.string.call_error_other
    })
    Ending.Ended, null -> stringResource(R.string.call_ended)
}

private enum class Look { PLAIN, PRESSED, HANG, ANSWER }

@Composable
private fun RoundButton(icon: List<String>, label: String, look: Look, c: Palette, pressed: Boolean? = null, onClick: () -> Unit) {
    val big = look == Look.HANG || look == Look.ANSWER
    val (fill, ink) = when (look) {
        Look.HANG -> c.hang to Color.White
        Look.ANSWER -> c.btn to Color.White
        Look.PRESSED -> c.text to c.bg
        Look.PLAIN -> c.surface to c.text
    }
    Column(Modifier.widthIn(min = 76.dp).clip(RoundedCornerShape(12.dp)).clickable(role = Role.Button, onClick = onClick)
        .semantics { if (pressed != null) selected = pressed }, horizontalAlignment = Alignment.CenterHorizontally) {
        // Every button takes the big one's height, so the labels under a row line up (as in the mockups).
        Box(Modifier.size(72.dp), contentAlignment = Alignment.Center) {
            Box(Modifier.size(if (big) 72.dp else 64.dp).clip(CircleShape).background(fill)
                .then(if (look == Look.PLAIN) Modifier.border(1.dp, c.border, CircleShape) else Modifier), contentAlignment = Alignment.Center) {
                Icon(icon, ink, if (big) 32.dp else 28.dp, rotate = look == Look.HANG)
            }
        }
        Spacer(Modifier.height(8.dp))
        BasicText(label, style = TextStyle(color = c.text, fontSize = 13.sp, textAlign = TextAlign.Center))
    }
}

@Composable
private fun Controls(v: CallView?, routes: Routes, c: Palette, act: CallActions, onChooseRoute: (Boolean) -> Unit) {
    // Ringing: decline and answer apart, toward the edges (mockups 22 to 24); otherwise one even row.
    val apart = v == null || v.phase == Phase.INCOMING
    Row(Modifier.fillMaxWidth().padding(bottom = 12.dp, start = if (apart) 8.dp else 0.dp, end = if (apart) 8.dp else 0.dp),
        horizontalArrangement = if (apart) Arrangement.SpaceBetween else Arrangement.SpaceEvenly, verticalAlignment = Alignment.Top) {
        when {
            v == null || v.phase == Phase.INCOMING -> {
                RoundButton(Icons.call, stringResource(R.string.call_decline), Look.HANG, c, onClick = act.hangUp)
                RoundButton(Icons.call, stringResource(R.string.call_answer), Look.ANSWER, c, onClick = act.answer)
            }
            v.phase == Phase.ENDED && v.ending?.problem == true ->
                RoundButton(Icons.close, stringResource(R.string.call_close), Look.PLAIN, c, onClick = act.close)
            v.phase == Phase.ENDED -> if (v.mine || v.ending is Ending.Talked) {
                RoundButton(Icons.call, stringResource(R.string.call_back), Look.ANSWER, c, onClick = act.callBack)
                RoundButton(Icons.logo, stringResource(R.string.call_message), Look.PLAIN, c, onClick = act.message)
            }
            else -> {
                // The left button: the speaker, or a choice of where the sound goes when a headset is there too (mockups 25, 26).
                val current = routes.current
                val onSpeaker = current?.kind == RouteKind.SPEAKER
                if (routes.choice) {
                    RoundButton(routeIcon(current?.kind), current?.takeIf { it.kind == RouteKind.BLUETOOTH }?.name ?: stringResource(R.string.call_speaker),
                        if (current?.kind == RouteKind.EARPIECE) Look.PLAIN else Look.PRESSED, c) { onChooseRoute(true) }
                } else {
                    RoundButton(Icons.speaker, stringResource(R.string.call_speaker), if (onSpeaker) Look.PRESSED else Look.PLAIN, c, pressed = onSpeaker) {
                        routes.toggle?.let(act.route)
                    }
                }
                RoundButton(Icons.call, stringResource(R.string.call_end), Look.HANG, c, onClick = act.hangUp)
                RoundButton(if (v.muted) Icons.micOff else Icons.mic, stringResource(R.string.call_mute), if (v.muted) Look.PRESSED else Look.PLAIN, c,
                    pressed = v.muted) { act.mute(!v.muted) }
            }
        }
    }
}

/** "Listen with": the phone, the speaker, a headset (mockup 26). */
@Composable
private fun RouteSheet(routes: Routes, c: Palette, dark: Boolean, onPick: (Route) -> Unit, onDismiss: () -> Unit) {
    Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = if (dark) 0.55f else 0.4f)).clickable(onClick = onDismiss)) {
        Column(Modifier.align(Alignment.BottomCenter).fillMaxWidth().clip(RoundedCornerShape(topStart = 20.dp, topEnd = 20.dp))
            .background(c.surface).clickable(enabled = false) {}.safeDrawingPadding().padding(20.dp)) {
            Box(Modifier.align(Alignment.CenterHorizontally).size(36.dp, 4.dp).clip(CircleShape).background(c.border))
            Spacer(Modifier.height(16.dp))
            BasicText(stringResource(R.string.call_listen_with), style = TextStyle(color = c.text, fontSize = 16.sp, fontWeight = FontWeight.Bold))
            Spacer(Modifier.height(8.dp))
            for (r in routes.list.filter { it.kind != RouteKind.OTHER }) {
                val on = r.id == routes.current?.id
                Row(Modifier.fillMaxWidth().height(56.dp).clip(RoundedCornerShape(12.dp)).clickable(role = Role.RadioButton) { onPick(r) }
                    .semantics { selected = on }, verticalAlignment = Alignment.CenterVertically) {
                    Icon(routeIcon(r.kind), c.text)
                    Spacer(Modifier.width(16.dp))
                    BasicText(when (r.kind) {
                        RouteKind.EARPIECE -> stringResource(R.string.call_route_phone)
                        RouteKind.SPEAKER -> stringResource(R.string.call_speaker)
                        RouteKind.HEADSET -> stringResource(R.string.call_route_headset)
                        else -> r.name
                    }, Modifier.weight(1f), style = TextStyle(color = c.text, fontSize = 16.sp, fontWeight = if (on) FontWeight.Bold else FontWeight.Normal))
                    if (on) Icon(Icons.check, c.btn)
                }
            }
        }
    }
}
