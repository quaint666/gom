plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

/** A build setting from ~/.gradle/gradle.properties or -P: the server's address and the Firebase values never live in the repository. */
fun setting(name: String): String = providers.gradleProperty(name).orNull?.trim().orEmpty()

/** The release this code is: the repository's VERSION file, the same number the server shows (docs/releases.md). */
val release: String = rootDir.resolve("../VERSION").readText().trim().also {
    require(Regex("""^\d+\.\d+\.\d+$""").matches(it)) { "VERSION must hold one release number, like 1.2.3" }
}

/** Google Play's versionCode, from the same number: 1.2.3 is 10203, so every release is higher than the one before it. */
val releaseCode: Int = release.split('.').map(String::toInt).let { (major, minor, patch) ->
    require(minor < 100 && patch < 100) { "VERSION's second and third numbers must stay below 100 for Play's versionCode" }
    major * 10_000 + minor * 100 + patch
}

/** Play's upload key (Google keeps the app's own signing key): from the build machine's Gradle properties, never this repository. */
val upload: Map<String, String> =
    listOf("storeFile", "storePassword", "keyAlias", "keyPassword").associateWith { setting("gom.upload.$it") }

/** A Java string literal for BuildConfig. */
fun literal(value: String): String = "\"" + value.replace("\\", "\\\\").replace("\"", "\\\"") + "\""

android {
    namespace = "io.github.quaint666.gom"
    compileSdk = 37

    defaultConfig {
        applicationId = "io.github.quaint666.gom"
        minSdk = 31
        // What Google Play requires since 31 August 2026; 37 waits until Play asks for it (docs/decisions.md).
        targetSdk = 36
        versionCode = releaseCode
        versionName = release
    }

    buildFeatures {
        compose = true
        buildConfig = true
        resValues = true
    }

    // One flavour per server: staging installs beside the real one. Each reads its own settings (README, "The Android app").
    flavorDimensions += "server"
    productFlavors {
        create("prod") {
            dimension = "server"
            resValue("string", "app_name", "Gom")
        }
        create("staging") {
            dimension = "server"
            applicationIdSuffix = ".staging"
            resValue("string", "app_name", "Gom staging")
        }
    }
    productFlavors.configureEach {
        val prefix = "gom.$name."
        buildConfigField("String", "BASE_URL", literal(setting(prefix + "baseUrl")))
        buildConfigField("String", "ALSO_ALLOW", literal(setting(prefix + "alsoAllow")))
        buildConfigField("String", "FIREBASE_PROJECT_ID", literal(setting(prefix + "firebase.projectId")))
        buildConfigField("String", "FIREBASE_APP_ID", literal(setting(prefix + "firebase.appId")))
        buildConfigField("String", "FIREBASE_API_KEY", literal(setting(prefix + "firebase.apiKey")))
        buildConfigField("String", "FIREBASE_SENDER_ID", literal(setting(prefix + "firebase.senderId")))
    }

    signingConfigs {
        create("upload") {
            if (upload.values.all(String::isNotEmpty)) {
                storeFile = file(upload.getValue("storeFile"))
                storePassword = upload.getValue("storePassword")
                keyAlias = upload.getValue("keyAlias")
                keyPassword = upload.getValue("keyPassword")
            }
        }
    }

    buildTypes {
        // WebRTC brings its native code for four kinds of processor; a debug build keeps the phones' (arm64) and the emulator's
        // (x86_64), so it stays small. A release keeps all four, and Play gives each phone only its own.
        debug {
            ndk { abiFilters += listOf("arm64-v8a", "x86_64") }
        }
        // A release goes to Google Play, signed with the upload key (checkUploadKey below). Not shrunk: R8 halves the download but
        // renames classes WebRTC's native code finds by name, so calls crash without extra rules (docs/decisions.md).
        release {
            signingConfig = signingConfigs.getByName("upload")
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    lint {
        abortOnError = true
        warningsAsErrors = true
        checkDependencies = true
        // Newer versions are chosen on purpose, when a pull request moves them; targetSdk follows Play's yearly rule (above).
        disable += setOf("GradleDependency", "NewerVersionAvailable", "AndroidGradlePluginVersion", "OldTargetApi")
        // Asks for androidx.fragment 1.3+ with the activity-result API, but the app has no fragments at all.
        disable += "InvalidFragmentVersionForActivityResult"
    }

    testOptions {
        unitTests.isReturnDefaultValues = true
    }
}

kotlin {
    compilerOptions {
        allWarningsAsErrors = true
    }
}

// A release is never built unsigned or with the debug key: a missing upload setting stops it before anything is built.
val uploadMissing = upload.filterValues(String::isEmpty).keys.map { "gom.upload.$it" }
val uploadStore = upload.getValue("storeFile").let { it.isNotEmpty() && file(it).isFile }
val checkUploadKey = tasks.register("checkUploadKey") {
    doFirst {
        if (uploadMissing.isNotEmpty()) {
            throw GradleException("A release is signed with Play's upload key; missing: ${uploadMissing.joinToString()}")
        }
        if (!uploadStore) throw GradleException("gom.upload.storeFile is not a file")
    }
}
tasks.matching { Regex("""pre\w+ReleaseBuild""").matches(it.name) }.configureEach { dependsOn(checkUploadKey) }

dependencies {
    implementation(libs.androidx.activity)
    implementation(libs.androidx.core)
    implementation(libs.androidx.webkit)
    // Calls (docs/voice-calls.md, PR 6): Android's call system, the service that keeps a call going, and the call's own socket.
    implementation(libs.androidx.core.telecom)
    implementation(libs.androidx.lifecycle.service)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.okhttp)
    implementation(libs.webrtc)
    // The call screens (6c): Compose without Material, drawn in Gom's own look.
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.foundation)
    implementation(libs.compose.ui)
    implementation(libs.androidx.activity.compose)
    // Previews of the call screens (src/debug, `android preview render`), in debug builds only.
    debugImplementation(libs.compose.ui.tooling)
    debugImplementation(libs.compose.ui.tooling.preview)
    implementation(platform(libs.firebase.bom))
    implementation(libs.firebase.messaging)

    testImplementation(libs.junit)
    // Android's own org.json is only a stub in local unit tests.
    testImplementation(libs.json)
    testImplementation(libs.kotlinx.coroutines.test)
}
