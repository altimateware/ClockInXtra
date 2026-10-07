package com.contoso.clockinxtra

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import java.io.File

/**
 * On-device signals that the operating system has been rooted or is not a
 * production build (§25).
 *
 * **What this is not.** A rooted device controls the process that runs these
 * checks, so every one of them can be hidden — Magisk's DenyList exists to do
 * exactly that. Client-side detection is a speed bump that stops casual use of a
 * rooted phone, and records the attempt; it is not a security boundary.
 *
 * The boundary is on the server: the key attestation it verifies at registration
 * is signed by the secure environment, not by this process, and it refuses a
 * device whose bootloader is unlocked or whose boot state is not Verified. Root
 * that survives these checks still has to survive that one.
 *
 * **Why not a third-party SDK.** The widely used commercial option sends telemetry
 * to its vendor's servers, and Google Play Integrity is a Google cloud service.
 * §2.1 rules out both. Everything here uses public Android APIs only — no hidden
 * system-property reflection, which newer Android releases block and which would
 * make the check itself the thing that breaks.
 */
class IntegrityHandler(private val context: Context) : MethodChannel.MethodCallHandler {

    private companion object {
        /** Where su binaries are conventionally installed by rooting tools. */
        val SU_PATHS = listOf(
            "/system/bin/su", "/system/xbin/su", "/sbin/su", "/system/su",
            "/system/bin/.ext/.su", "/system/usr/we-need-root/su-backup",
            "/data/local/su", "/data/local/bin/su", "/data/local/xbin/su",
            "/su/bin/su", "/system/sbin/su", "/vendor/bin/su",
        )

        /**
         * Root management apps. Declared in the manifest's `<queries>` element —
         * without that, Android 11+ package visibility hides them and this check
         * silently finds nothing. Magisk can repackage itself under a random name,
         * which this cannot see; that is one of the documented bypasses.
         */
        val ROOT_PACKAGES = listOf(
            "com.topjohnwu.magisk", "eu.chainfire.supersu", "com.noshufou.android.su",
            "com.koushikdutta.superuser", "com.kingroot.kinguser", "me.phh.superuser",
            "com.thirdparty.superuser", "com.yellowes.su",
        )
    }

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        when (call.method) {
            "inspect" -> try {
                result.success(inspect())
            } catch (error: Exception) {
                // A check that cannot run is reported, not treated as a pass.
                result.error("INTEGRITY_CHECK_FAILED", error.message, null)
            }
            else -> result.notImplemented()
        }
    }

    private fun inspect(): Map<String, Any> {
        val indicators = mutableListOf<String>()

        SU_PATHS.firstOrNull { File(it).exists() }?.let { indicators.add("SU_BINARY_PRESENT") }

        // A production OS is signed with the manufacturer's release keys. test-keys
        // means a custom or development build, which also cannot pass the verified
        // boot check the server applies.
        if (Build.TAGS?.contains("test-keys") == true) {
            indicators.add("TEST_KEYS_BUILD")
        }

        // "userdebug" and "eng" builds allow adb to run as root.
        if (Build.TYPE != "user") {
            indicators.add("NON_PRODUCTION_OS_BUILD")
        }

        if (ROOT_PACKAGES.any(::isInstalled)) {
            indicators.add("ROOT_MANAGER_INSTALLED")
        }

        return mapOf(
            "indicators" to indicators,
            "isEmulator" to isEmulator(),
            "osBuildType" to (Build.TYPE ?: "unknown"),
        )
    }

    private fun isInstalled(packageName: String): Boolean = try {
        context.packageManager.getPackageInfo(packageName, 0)
        true
    } catch (_: PackageManager.NameNotFoundException) {
        false
    }

    /** Reported for diagnosis; not an indicator of root on its own. */
    private fun isEmulator(): Boolean =
        Build.FINGERPRINT.startsWith("generic") ||
            Build.FINGERPRINT.contains("emulator") ||
            Build.HARDWARE in setOf("goldfish", "ranchu") ||
            Build.PRODUCT.contains("sdk")
}
