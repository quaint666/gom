package io.github.quaint666.gom.calls

import android.app.ActivityManager
import android.app.AlertDialog
import android.app.NotificationManager
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.os.Build
import android.provider.Settings
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.edit
import androidx.core.net.toUri
import io.github.quaint666.gom.R

/**
 * What can stop a call ringing on this phone, said once each (docs/voice-calls.md, decision 35 and section 7): Android turned
 * full-screen ringing off for the app (Android 14+, a setting the person or the store can change), and a phone that puts apps to
 * sleep (Samsung's "Sleeping apps", or any phone's battery restriction), where a ringing push may never wake Gom. Each opens the
 * right settings page. Only once calls can ring at all: this build takes calls and notifications are on.
 */
object Readiness {
    private const val PREFS = "readiness"
    private const val FULL_SCREEN = "fullScreen"
    private const val SLEEP = "sleep"

    /** What to say now, if anything: at most one thing a time, and each only once. */
    fun next(context: Context): Need? {
        if (!CallService.AVAILABLE || !NotificationManagerCompat.from(context).areNotificationsEnabled()) return null
        val said = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val fullScreenOff = Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE &&
            !context.getSystemService(NotificationManager::class.java).canUseFullScreenIntent()
        if (fullScreenOff && !said.getBoolean(FULL_SCREEN, false)) return Need.FullScreen
        val sleeps = context.getSystemService(ActivityManager::class.java).isBackgroundRestricted || Build.MANUFACTURER.equals("samsung", ignoreCase = true)
        if (sleeps && !said.getBoolean(SLEEP, false)) return Need.Sleep
        return null
    }

    enum class Need(val title: Int, val text: Int, val key: String) {
        FullScreen(R.string.ready_full_screen_title, R.string.ready_full_screen_text, FULL_SCREEN),
        Sleep(R.string.ready_sleep_title, R.string.ready_sleep_text, SLEEP),
    }

    /** Says [need] in a plain dialog, with a button to the settings page that fixes it. */
    fun show(context: Context, need: Need) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit { putBoolean(need.key, true) }
        AlertDialog.Builder(context)
            .setTitle(need.title)
            .setMessage(need.text)
            .setPositiveButton(R.string.ready_open_settings) { _, _ -> settings(context, need) }
            .setNegativeButton(R.string.ready_later, null)
            .show()
    }

    private fun settings(context: Context, need: Need) {
        val app = "package:${context.packageName}".toUri()
        val intent = if (need == Need.FullScreen && Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
            Intent(Settings.ACTION_MANAGE_APP_USE_FULL_SCREEN_INTENT, app)
        else Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, app)   // the app's page: Battery is one tap from it
        try { context.startActivity(intent) } catch (_: ActivityNotFoundException) {
            context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, app))
        }
    }
}
