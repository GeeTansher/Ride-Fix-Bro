plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
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
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.7.3")

    // Jetpack Compose MVVM support (ViewModel ko UI se jodne ke liye)
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.4")

    // Coil (Image loading ke liye, baad mein photos dikhane kaam aayega)
    implementation("io.coil-kt:coil-compose:2.6.0")

    // Compose ke mast icons ke liye (Send button waghera)
    implementation("androidx.compose.material:material-icons-extended:1.6.8")

    // Markdown parsing ke liye
    implementation("com.mikepenz:multiplatform-markdown-renderer-m3:0.24.0")
}