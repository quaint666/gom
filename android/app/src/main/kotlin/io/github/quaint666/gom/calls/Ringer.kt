package io.github.quaint666.gom.calls

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.Ringtone
import android.media.RingtoneManager
import android.media.ToneGenerator
import android.os.Build
import android.os.VibrationAttributes
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager

/**
 * The sounds of a call, which an app that runs its own calls plays itself (Android's call system rings only the phone's own calls):
 * the phone's ringtone and vibration while a call rings here, as the phone's ring, vibrate or silent setting says; and the ringback
 * tone the caller hears while the other phone rings, as the page plays it (`calls/ring.ts`).
 */
class Ringer(context: Context) {
    private val app = context.applicationContext
    private val audio = app.getSystemService(AudioManager::class.java)
    private val vibrator: Vibrator = app.getSystemService(VibratorManager::class.java).defaultVibrator
    private var ringtone: Ringtone? = null
    private var tone: ToneGenerator? = null
    private var vibrating = false

    /** A call rings here: the ringtone, looping, and the vibration, as the phone is set; nothing on silent. */
    fun incoming() {
        if (ringtone != null || vibrating) return
        stop()
        val mode = audio.ringerMode
        if (mode == AudioManager.RINGER_MODE_SILENT) return
        if (mode == AudioManager.RINGER_MODE_NORMAL) {
            ringtone = RingtoneManager.getRingtone(app, RingtoneManager.getActualDefaultRingtoneUri(app, RingtoneManager.TYPE_RINGTONE)
                ?: android.provider.Settings.System.DEFAULT_RINGTONE_URI)?.apply {
                audioAttributes = AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_NOTIFICATION_RINGTONE)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION).build()
                isLooping = true
                play()
            }
        }
        if (vibrator.hasVibrator()) {
            val pattern = VibrationEffect.createWaveform(VIBRATION, 0)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) vibrator.vibrate(pattern, VibrationAttributes.createForUsage(VibrationAttributes.USAGE_RINGTONE))
            else @Suppress("DEPRECATION") vibrator.vibrate(pattern, AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_NOTIFICATION_RINGTONE).build())
            vibrating = true
        }
    }

    /** The other phone rings: the ringback tone, in the call's own sound (earpiece or speaker, where the call goes). */
    fun ringback() {
        if (tone != null) return
        stop()
        tone = try {
            ToneGenerator(AudioManager.STREAM_VOICE_CALL, RINGBACK_VOLUME).apply { startTone(ToneGenerator.TONE_SUP_RINGTONE) }
        } catch (_: RuntimeException) { null }   // the tone generator is busy or missing: the call goes on without it
    }

    fun stop() {
        ringtone?.stop()
        ringtone = null
        tone?.stopTone()
        tone?.release()
        tone = null
        if (vibrating) vibrator.cancel()
        vibrating = false
    }

    private companion object {
        /** A second of vibration, a second of rest, repeated. */
        val VIBRATION = longArrayOf(0, 1_000, 1_000)
        const val RINGBACK_VOLUME = 80
    }
}
