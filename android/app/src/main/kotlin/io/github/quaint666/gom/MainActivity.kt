package io.github.quaint666.gom

import android.Manifest
import android.annotation.SuppressLint
import android.app.DownloadManager
import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.webkit.CookieManager
import android.webkit.RenderProcessGoneDetail
import android.webkit.URLUtil
import android.webkit.ValueCallback
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.app.NotificationManagerCompat
import androidx.core.net.toUri
import androidx.core.view.ContentInfoCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.core.view.updateLayoutParams
import androidx.core.view.updatePadding
import io.github.quaint666.gom.calls.CallService
import io.github.quaint666.gom.calls.Readiness

/**
 * Gom in a web view, and only Gom (docs/voice-calls.md, decision 31): its own pages load inside, every other address opens in
 * the browser. Around it, what a browser would give the page: picking files, downloads, full-screen video, the back button, and
 * the system bars in the page's colours. Notifications go through [Bridge] and [Registration].
 */
class MainActivity : ComponentActivity() {
    private val origins = GomApp.origins
    private lateinit var root: FrameLayout
    private lateinit var top: View
    private lateinit var web: GomWebView
    private lateinit var bridge: Bridge
    private lateinit var offline: View
    private var picking: ValueCallback<Array<Uri>>? = null
    private var fullScreen: View? = null

    /** The largest picture the open page's server takes (its hello says); 0 until then, and no picture is taken. */
    private var imageBytes = 0L

    private val pick = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val data = result.data
        val clips = data?.clipData
        val chosen = when {
            result.resultCode != RESULT_OK -> null
            clips != null -> Array(clips.itemCount) { clips.getItemAt(it).uri }
            else -> WebChromeClient.FileChooserParams.parseResult(result.resultCode, data)
        }
        picking?.onReceiveValue(chosen)   // always answered, or the page's file input stops working
        picking = null
    }

    /** A call the page asked for, waiting for Android's microphone question. */
    private var dialling: FromPage.Call? = null

    private val askMicrophone = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        val call = dialling
        dialling = null
        if (granted && call != null) CallService.dial(this, call.conversation, call.name, call.avatar)
        // Refused (or refused for good, when Android no longer asks): said, as the page says it in a browser.
        else if (call != null) android.widget.Toast.makeText(this, R.string.call_need_mic, android.widget.Toast.LENGTH_LONG).show()
    }

    private val askNotifications = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        Registration.markAsked(this)
        if (granted) register() else bridge.send(ToPage.denied)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        if (!origins.configured) {
            // A build made without gom.<flavour>.baseUrl: say so rather than show an empty page.
            setContentView(TextView(this).apply { setText(R.string.not_configured); setPadding(48, 160, 48, 48) })
            return
        }
        root = FrameLayout(this)
        top = View(this)
        web = GomWebView(this).apply { onImage = { content -> keyboardImage(content) } }
        root.addView(web, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        root.addView(top, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0))
        offline = offlineView()
        root.addView(offline, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        setContentView(root)
        paint(getColor(R.color.gom), getColor(R.color.page))

        // The page sits between the system bars and above the keyboard; the strip under the status bar takes the page's colour.
        ViewCompat.setOnApplyWindowInsetsListener(root) { _, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
            val keyboard = insets.getInsets(WindowInsetsCompat.Type.ime())
            top.updateLayoutParams { height = bars.top }
            root.updatePadding(left = bars.left, right = bars.right, bottom = maxOf(bars.bottom, keyboard.bottom))
            web.updateLayoutParams<FrameLayout.LayoutParams> { topMargin = bars.top }
            WindowInsetsCompat.CONSUMED
        }

        setUp()
        bridge = Bridge(origins.origin) { message, from -> onPage(message, from) }
        bridge.attach(web)
        Registration.listener = { fid -> bridge.send(ToPage.fid(fid)) }

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                when {
                    fullScreen != null -> web.webChromeClient?.onHideCustomView()
                    web.canGoBack() -> web.goBack()
                    else -> { isEnabled = false; onBackPressedDispatcher.onBackPressed() }
                }
            }
        })

        if (savedInstanceState == null || web.restoreState(savedInstanceState) == null) web.loadUrl(start(intent))
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        if (origins.configured) web.loadUrl(start(intent))
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        if (origins.configured) web.saveState(outState)
    }

    override fun onResume() {
        super.onResume()
        // What could stop calls ringing here, said once each (calls/Readiness.kt).
        if (origins.configured) Readiness.next(this)?.let { Readiness.show(this, it) }
    }

    /** "Can't reach Gom", over the page, with a button that loads it again. */
    private fun offlineView(): View = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        gravity = Gravity.CENTER
        setPadding(64, 64, 64, 64)
        setBackgroundColor(getColor(R.color.page))
        visibility = View.GONE
        isClickable = true   // nothing under it is touched
        addView(TextView(context).apply {
            setText(R.string.offline_title); textSize = 20f; setTextColor(getColor(R.color.call_text)); gravity = Gravity.CENTER
        })
        addView(TextView(context).apply {
            setText(R.string.offline_text); textSize = 15f; setTextColor(getColor(R.color.call_muted)); gravity = Gravity.CENTER
            setPadding(0, 16, 0, 32)
        })
        addView(Button(context).apply {
            setText(R.string.offline_retry)
            isAllCaps = false
            setOnClickListener { offline.visibility = View.GONE; web.reload() }
        }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT))
    }

    override fun onPause() {
        super.onPause()
        CookieManager.getInstance().flush()   // the session is kept even if Android stops the app now
    }

    override fun onDestroy() {
        if (origins.configured) {
            Registration.listener = null
            web.destroy()
        }
        super.onDestroy()
    }

    /** Where a notification's tap points, when it is Gom's; else the start page. */
    private fun start(intent: Intent?): String {
        val asked = intent?.data?.toString()
        return if (asked != null && origins.isGom(asked)) asked else origins.page(null)
    }

    @SuppressLint("SetJavaScriptEnabled")   // Gom is a JavaScript app; only its own origin loads, and the bridge is origin-bound
    private fun setUp() {
        web.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            allowFileAccess = false
            allowContentAccess = false
            mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
            mediaPlaybackRequiresUserGesture = true
            setSupportMultipleWindows(false)
        }
        CookieManager.getInstance().setAcceptCookie(true)
        CookieManager.getInstance().setAcceptThirdPartyCookies(web, false)

        web.webViewClient = object : WebViewClient() {
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                val url = request.url.toString()
                if (origins.loadsInside(url)) return false
                outside(request.url)
                return true
            }

            // Gom could not be reached (no network, the server away): the app's own words and a retry, not the web view's error page.
            override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
                if (request.isForMainFrame) offline.visibility = View.VISIBLE
            }

            // The page's renderer died (Android reclaimed it): start again rather than let the app close.
            override fun onRenderProcessGone(view: WebView, detail: RenderProcessGoneDetail): Boolean {
                recreate()
                return true
            }
        }

        web.webChromeClient = object : WebChromeClient() {
            override fun onShowFileChooser(view: WebView, callback: ValueCallback<Array<Uri>>, params: FileChooserParams): Boolean {
                picking?.onReceiveValue(null)
                picking = callback
                val intent = params.createIntent()
                if (params.mode == FileChooserParams.MODE_OPEN_MULTIPLE) intent.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
                return try { pick.launch(intent); true } catch (_: ActivityNotFoundException) { picking = null; false }
            }

            // A video's own full-screen button.
            override fun onShowCustomView(view: View, callback: CustomViewCallback) {
                fullScreen?.let { onHideCustomView() }
                fullScreen = view
                (window.decorView as ViewGroup).addView(view, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
                WindowCompat.getInsetsController(window, view).apply {
                    systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                    hide(WindowInsetsCompat.Type.systemBars())
                }
            }

            override fun onHideCustomView() {
                val view = fullScreen ?: return
                (window.decorView as ViewGroup).removeView(view)
                fullScreen = null
                WindowCompat.getInsetsController(window, root).show(WindowInsetsCompat.Type.systemBars())
            }
        }

        // A file from Gom is saved by Android's download manager, with the session, into Downloads.
        web.setDownloadListener { url, userAgent, disposition, mime, _ ->
            if (!origins.isGom(url)) { outside(url.toUri()); return@setDownloadListener }
            val name = URLUtil.guessFileName(url, disposition, mime)
            val request = DownloadManager.Request(url.toUri())
                .addRequestHeader("Cookie", CookieManager.getInstance().getCookie(url).orEmpty())
                .addRequestHeader("User-Agent", userAgent)
                .setMimeType(mime)
                .setTitle(name)
                .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
                .setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, name)
            getSystemService(DownloadManager::class.java).enqueue(request)
        }
    }

    private fun outside(uri: Uri) {
        try { startActivity(Intent(Intent.ACTION_VIEW, uri)) } catch (_: ActivityNotFoundException) { /* nothing on the phone opens it */ }
    }

    private fun onPage(message: FromPage, from: Bridge) {
        when (message) {
            is FromPage.Hello -> {
                imageBytes = message.images
                // Calls ring by FCM, so a build without it takes none.
                from.send(ToPage.hello(appVersion(), GomApp.push, permission(), Registration.fid(this), calls = CallService.AVAILABLE && GomApp.push))
            }
            FromPage.Enable -> enable()
            FromPage.Disable -> Registration.unregister(this)
            is FromPage.Clear -> Notices.clear(this, message.conversation)
            is FromPage.Theme -> paint(message.bar ?: getColor(R.color.gom), message.page ?: getColor(R.color.page))
            is FromPage.Call -> call(message)
        }
    }

    /**
     * As the browser asks: Android's question first, then FCM. Android 12 has no question (notifications are on unless the person
     * turned them off in its settings), so there only that switch counts.
     */
    private fun enable() {
        when {
            !GomApp.push -> bridge.send(ToPage.failed)
            NotificationManagerCompat.from(this).areNotificationsEnabled() -> register()
            Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU -> bridge.send(ToPage.denied)
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED -> register()
            else -> askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    /** The page's call button: Android's microphone question first, as a browser asks before a call. */
    private fun call(call: FromPage.Call) {
        if (!CallService.AVAILABLE) return
        if (CallService.hasMic(this)) CallService.dial(this, call.conversation, call.name, call.avatar)
        else { dialling = call; askMicrophone.launch(Manifest.permission.RECORD_AUDIO) }
    }

    private fun register() = Registration.register(this) { fid -> bridge.send(if (fid != null) ToPage.enabled(fid) else ToPage.failed) }

    /** Android's answer, in the browser's words: a "no" after the question was asked is "denied", as a browser's block is. */
    private fun permission(): String = when {
        NotificationManagerCompat.from(this).areNotificationsEnabled() -> "granted"
        Registration.asked(this) || Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU -> "denied"
        else -> "default"
    }

    private fun appVersion(): String = packageManager.getPackageInfo(packageName, 0).versionName.orEmpty()

    /**
     * A picture the keyboard committed (Gboard's GIF button): read at once, while Android lets the app read it, and sent to the
     * page, which takes it as a paste. Larger than the server takes, or of another kind, it is not taken.
     */
    private fun keyboardImage(content: ContentInfoCompat): Boolean {
        if (imageBytes <= 0) return false
        val clip = content.clip
        var sent = false
        for (i in 0 until clip.itemCount) {
            val uri = clip.getItemAt(i).uri ?: continue
            val type = contentResolver.getType(uri) ?: clip.description.getMimeType(0)
            val bytes = try {
                contentResolver.openInputStream(uri)?.use { readAtMost(it, imageBytes) }
            } catch (_: java.io.IOException) { null } catch (_: SecurityException) { null } ?: continue
            val message = ToPage.image(type, bytes) ?: continue
            bridge.send(message)
            sent = true
        }
        return sent
    }

    /** The status bar's strip takes the page's main colour, the rest its background; the icons are dark or light to suit. */
    private fun paint(bar: Int, page: Int) {
        top.setBackgroundColor(bar)
        root.setBackgroundColor(page)
        window.decorView.setBackgroundColor(page)
        WindowCompat.getInsetsController(window, root).apply {
            isAppearanceLightStatusBars = Colors.isLight(bar)
            isAppearanceLightNavigationBars = Colors.isLight(page)
        }
    }
}
