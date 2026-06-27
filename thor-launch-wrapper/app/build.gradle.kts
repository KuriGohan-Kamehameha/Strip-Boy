plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
}

android {
    namespace = "com.moonbench.thorlaunch"
    compileSdk {
        version = release(36)
    }

    defaultConfig {
        applicationId = "com.moonbench.thorlaunch"
        minSdk = 33
        targetSdk = 36
        versionCode = 1
        versionName = "1.0"

        // The bundled NativeAOT companion patcher is arm64-only (AYN Thor).
        ndk {
            abiFilters += "arm64-v8a"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            isShrinkResources = false
        }
    }

    // Ship libpipboy-patcher.so uncompressed + extracted to nativeLibraryDir so
    // it can be exec'd on-device (API 33 blocks exec from writable app dirs).
    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }
    kotlinOptions {
        jvmTarget = "11"
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.appcompat)
}
