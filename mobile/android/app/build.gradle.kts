import java.util.Properties

plugins {
    id("com.android.application")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

// Release signing material, read from a file that is NOT in source control (§47).
//
// android/key.properties is listed in .gitignore and holds:
//
//     storeFile=C:/keys/clockinxtra-release.jks
//     storePassword=...
//     keyAlias=clockinxtra
//     keyPassword=...
//
// This is not ceremony. The server checks the app's signing certificate digest
// inside the Android key attestation (ExpectedSigningCertificateDigests), so the
// signing key is part of the security boundary, not just a publishing detail.
val keystorePropertiesFile = rootProject.file("key.properties")
val keystoreProperties = Properties().apply {
    if (keystorePropertiesFile.exists()) {
        keystorePropertiesFile.inputStream().use { load(it) }
    }
}

android {
    namespace = "com.contoso.clockinxtra"
    compileSdk = flutter.compileSdkVersion
    ndkVersion = flutter.ndkVersion

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        // The package name is not cosmetic: it is checked inside the Android key
        // attestation the server verifies, alongside the signing certificate
        // digest. Changing it means reconfiguring Attestation:Android:
        // ExpectedPackageName on the API, or every registration is refused.
        applicationId = "com.contoso.clockinxtra"

        // Android 9 (API 28) is the floor recommended in OPEN-31, and this is
        // where the reason bites: setUnlockedDeviceRequired and StrongBox-backed
        // keys both begin at 28. Below it the device key could be used while the
        // handset is locked, which would weaken the binding this whole design
        // rests on. Devices below the minimum cannot register.
        minSdk = 28

        targetSdk = flutter.targetSdkVersion
        // Uses the version code from pubspec.yaml. When using split APKs, 1000 * ABI_VERSION
        // is added automatically by Flutter. (https://developer.android.com/studio/build/configure-apk-splits#configure-APK-versions)
        // You can force using the value of versionCode by specifying the `-P force-version-code-ignoring-abi=true`
        // flag during build.
        versionCode = flutter.versionCode
        versionName = flutter.versionName
    }

    signingConfigs {
        if (keystorePropertiesFile.exists()) {
            create("release") {
                storeFile = file(keystoreProperties.getProperty("storeFile"))
                storePassword = keystoreProperties.getProperty("storePassword")
                keyAlias = keystoreProperties.getProperty("keyAlias")
                keyPassword = keystoreProperties.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            // The Flutter template signs release builds with the debug key so
            // that `flutter run --release` works out of the box. That is exactly
            // wrong here. The debug keystore ships with the Android SDK and its
            // password is public, so a release APK signed with it carries a
            // signing certificate anyone can reproduce — and the server treats
            // that certificate's digest as evidence the app is genuine. Whoever
            // then added the debug digest to the allow-list to make registration
            // work would have handed device enrolment to anyone with an SDK.
            //
            // So: signed with the organisation's key, or not signed at all.
            signingConfig = signingConfigs.findByName("release")
        }
    }
}

// Fails the build rather than quietly producing an unsigned release APK, which
// is the kind of artefact that gets discovered at the point someone tries to
// install it.
gradle.taskGraph.whenReady {
    val buildingRelease = allTasks.any { task ->
        task.project == project && task.name.contains("Release") && !task.name.contains("Debug")
    }

    if (buildingRelease && !keystorePropertiesFile.exists()) {
        throw GradleException(
            "A release build needs android/key.properties with the organisation's signing key. " +
                "See docs/deployment for how it is provisioned. Release builds are never signed " +
                "with the debug key: the server checks the signing certificate digest inside the " +
                "device attestation."
        )
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

flutter {
    source = "../.."
}
