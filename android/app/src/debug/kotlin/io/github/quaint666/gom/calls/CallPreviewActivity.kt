package io.github.quaint666.gom.calls

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge

/**
 * Debug builds only: shows one of [CallScreenPreviews]' screens full size on a phone or emulator, with no call and no server
 * (`adb shell am start -n <package>/io.github.quaint666.gom.calls.CallPreviewActivity --es screen 25`).
 */
class CallPreviewActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        val screen = intent.getStringExtra("screen").orEmpty()
        setContent { previews[screen]?.invoke() ?: Calling() }
    }
}
