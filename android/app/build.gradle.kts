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
        versionCode = setting("gom.versionCode").ifEmpty { "1" }.toInt()
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

dependencies {
    implementation(libs.androidx.activity)
    implementation(libs.androidx.core)
    implementation(libs.androidx.webkit)
    // Calls (docs/voice-calls.md, PR 6): Android's call system, the service that keeps a call going, and the call's own socket.
    implementation(libs.androidx.core.telecom)
    implementation(libs.androidx.lifecycle.service)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.okhttp)
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
