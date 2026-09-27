import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

val localProperties = Properties()
val localPropertiesFile = rootProject.file("local.properties")
if (localPropertiesFile.exists()) {
    localPropertiesFile.inputStream().use { localProperties.load(it) }
}

// local.properties mein ye PUBLIC values rakhna:
// SUPABASE_URL
// SUPABASE_PUBLISHABLE_KEY
// GOOGLE_WEB_CLIENT_ID
fun publicConfig(name: String): String {
    val value = providers.environmentVariable(name).orNull ?: localProperties.getProperty(name, "")
    return "\"" + value.replace("\\", "\\\\").replace("\"", "\\\"") + "\""
}

android {
    namespace = "com.example.ridefixbro"
    compileSdk {
        version = release(37)
    }

    defaultConfig {
        applicationId = "com.example.ridefixbro"
        minSdk = 24
        targetSdk = 37
        versionCode = 1
        versionName = "1.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        buildConfigField("String", "SUPABASE_URL", publicConfig("SUPABASE_URL"))
        buildConfigField("String", "SUPABASE_PUBLISHABLE_KEY", publicConfig("SUPABASE_PUBLISHABLE_KEY"))
        buildConfigField("String", "GOOGLE_WEB_CLIENT_ID", publicConfig("GOOGLE_WEB_CLIENT_ID"))
    }

    buildTypes {
        release {
            optimization {
                enable = false
            }
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }
    buildFeatures {
        compose = true
        buildConfig = true
    }
}

dependencies {
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    testImplementation(libs.junit)
    testImplementation("io.mockk:mockk:1.14.6")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.10.2")
    androidTestImplementation(platform(libs.androidx.compose.bom))
    androidTestImplementation(libs.androidx.compose.ui.test.junit4)
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(libs.androidx.junit)
    debugImplementation(libs.androidx.compose.ui.test.manifest)
    debugImplementation(libs.androidx.compose.ui.tooling)

    // Networking - Retrofit & Gson (Tera API se baat karne ka jugaad)
    implementation("com.squareup.retrofit2:retrofit:2.9.0")
    implementation("com.squareup.retrofit2:converter-gson:2.9.0")

    // Coroutines (Background mein API call karne ke liye taaki app hang na ho)
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")

    implementation(platform("io.github.jan-tennert.supabase:bom:3.2.6"))
    implementation("io.github.jan-tennert.supabase:auth-kt")
    implementation("io.ktor:ktor-client-okhttp:3.3.1")
    implementation("androidx.credentials:credentials:1.6.0")
    implementation("androidx.credentials:credentials-play-services-auth:1.6.0")
    // Compatible with this project's Kotlin 2.2 compiler.
    implementation("com.google.android.libraries.identity.googleid:googleid:1.1.1")

    // Jetpack Compose MVVM support (ViewModel ko UI se jodne ke liye)
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.4")

    // Coil (Image loading ke liye, baad mein photos dikhane kaam aayega)
    implementation("io.coil-kt:coil-compose:2.6.0")

    // Compose ke mast icons ke liye (Send button waghera)
    implementation("androidx.compose.material:material-icons-extended:1.6.8")

    // Markdown parsing ke liye
    implementation("com.mikepenz:multiplatform-markdown-renderer-m3:0.24.0")
}