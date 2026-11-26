package io.github.quaint666.gom

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import com.google.firebase.FirebaseApp
import com.google.firebase.FirebaseOptions

/** Starts what the whole app shares: the server's origin rule, Firebase (when this build has its settings) and the channels. */
class GomApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Notices.channels(this)
        if (push) {
            // Started here rather than from google-services.json, so the values stay out of the repository (README).
            val options = FirebaseOptions.Builder()
                .setProjectId(BuildConfig.FIREBASE_PROJECT_ID)
                .setApplicationId(BuildConfig.FIREBASE_APP_ID)
                .setApiKey(BuildConfig.FIREBASE_API_KEY)
                .setGcmSenderId(BuildConfig.FIREBASE_SENDER_ID)
                .build()
            if (FirebaseApp.getApps(this).isEmpty()) FirebaseApp.initializeApp(this, options)
        }
    }

    companion object {
        val origins = Origins(BuildConfig.BASE_URL, BuildConfig.ALSO_ALLOW)

        /** Whether this build can get notifications: all four Firebase values were given when it was built. */
        val push: Boolean = listOf(BuildConfig.FIREBASE_PROJECT_ID, BuildConfig.FIREBASE_APP_ID, BuildConfig.FIREBASE_API_KEY,
            BuildConfig.FIREBASE_SENDER_ID).all { it.isNotEmpty() }
    }
}

/** The two kinds of notification, each its own channel so a person can tune them apart in Android's settings. */
internal fun Notices.channels(app: Application) {
    val manager = app.getSystemService(NotificationManager::class.java)
    manager.createNotificationChannel(
        NotificationChannel(MESSAGES, app.getString(R.string.channel_messages), NotificationManager.IMPORTANCE_HIGH))
    manager.createNotificationChannel(
        NotificationChannel(OWNER, app.getString(R.string.channel_owner), NotificationManager.IMPORTANCE_DEFAULT))
}
