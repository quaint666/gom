package io.github.quaint666.gom.calls

import android.graphics.BitmapFactory
import android.webkit.CookieManager
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import java.io.IOException
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.Request

/**
 * The other person's picture on the call screen: fetched with the app's own sign-in (the web view's cookie, as [HttpCallServer] does),
 * by the picture's id alone, and kept while the app runs. None, or one that fails to come, leaves the initials.
 */
object CallPictures {
    private val kept = ConcurrentHashMap<String, ImageBitmap>()

    suspend fun load(origin: String, avatar: String): ImageBitmap? {
        if (origin.isEmpty() || !CallProtocol.isId(avatar)) return null
        kept[avatar]?.let { return it }
        return withContext(Dispatchers.IO) {
            try {
                val request = Request.Builder().url("$origin/api/avatars/$avatar").get()
                    .apply { CookieManager.getInstance().getCookie(origin)?.let { header("Cookie", it) } }.build()
                HttpCallServer.shared.newCall(request).execute().use { r ->
                    if (!r.isSuccessful) return@use null
                    val bytes = r.body.bytes()
                    BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap()?.also { kept[avatar] = it }
                }
            } catch (_: IOException) { null }
        }
    }
}
