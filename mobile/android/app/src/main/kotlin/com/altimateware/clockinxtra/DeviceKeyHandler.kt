package com.altimateware.clockinxtra

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import java.io.ByteArrayOutputStream
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.interfaces.ECPublicKey

/**
 * Hardware-backed signing key, held in the Android Keystore.
 *
 * The private key is generated inside the Trusted Execution Environment — or a
 * StrongBox secure element where the device has one — and is **never**
 * extractable. The application can ask for a signature; it cannot obtain the key.
 * That is the whole basis of device binding: a copy of the app's storage, or the
 * app itself running under a debugger, yields nothing an attacker can sign with
 * somewhere else.
 *
 * See `docs/architecture/03-solution-architecture.md` §6.1.
 */
class DeviceKeyHandler : MethodChannel.MethodCallHandler {

    private companion object {
        const val KEY_ALIAS = "com.altimateware.clockinxtra.device"
        const val KEYSTORE = "AndroidKeyStore"

        /** An uncompressed P-256 point is 0x04 followed by two 32-byte integers. */
        const val COORDINATE_BYTES = 32
    }

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        try {
            when (call.method) {
                "hasKey" -> result.success(loadPrivateKey() != null)
                "generateKey" -> result.success(generateKey(call.argument<ByteArray>("challenge")))
                "sign" -> result.success(sign(call.argument<ByteArray>("data")))
                "deleteKey" -> result.success(deleteKey())
                else -> result.notImplemented()
            }
        } catch (error: Exception) {
            // Every failure reaches Dart as a coded error rather than an
            // exception crossing the channel, so the app can tell "this device
            // cannot do hardware keys" from "something went wrong".
            result.error(errorCode(error), error.message, null)
        }
    }

    /**
     * Generates a fresh key bound to the server's challenge.
     *
     * Generating discards any previous key, so the device must register again
     * afterwards. That is deliberate and not recoverable: the old private key is
     * gone from the secure element, and no backup of it exists anywhere.
     */
    private fun generateKey(challenge: ByteArray?): Map<String, Any> {
        require(challenge != null && challenge.isNotEmpty()) { "A challenge is required." }

        deleteKey()

        // StrongBox is a discrete security chip and is not present on every
        // device. It is requested first and the TEE used where it is absent,
        // rather than refusing hardware that is otherwise perfectly acceptable.
        val keyPair = try {
            generateInKeystore(challenge, useStrongBox = true)
        } catch (_: StrongBoxUnavailableException) {
            generateInKeystore(challenge, useStrongBox = false)
        }

        val certificates = KeyStore.getInstance(KEYSTORE)
            .apply { load(null) }
            .getCertificateChain(KEY_ALIAS)
            ?: throw IllegalStateException("The keystore returned no attestation chain.")

        return mapOf(
            "publicKey" to uncompressedPoint(keyPair.public as ECPublicKey),
            // A DER SEQUENCE OF Certificate, leaf first — the shape the server's
            // AndroidKeyAttestationVerifier parses.
            "attestation" to derSequenceOf(certificates.map { it.encoded }),
            "keyId" to ByteArray(0), // Apple's concept; Android has no equivalent.
        )
    }

    private fun generateInKeystore(challenge: ByteArray, useStrongBox: Boolean) =
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, KEYSTORE).apply {
            val builder = KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_SIGN)
                .setAlgorithmParameterSpec(java.security.spec.ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_SHA256)

                // Binds the attestation to this registration attempt. Without it
                // a previously captured attestation would be replayable.
                .setAttestationChallenge(challenge)

                // The key is unusable while the device is locked, so a lost
                // handset cannot sign attendance until somebody unlocks it.
                .setUnlockedDeviceRequired(true)

            if (useStrongBox && Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                builder.setIsStrongBoxBacked(true)
            }

            initialize(builder.build())
        }.generateKeyPair()

    /**
     * Signs with the device key, returning raw `r‖s`.
     *
     * Android's `SHA256withECDSA` produces a DER-encoded signature. RFC 9421
     * §3.3.4 specifies the raw concatenated form for `ecdsa-p256-sha256`, so the
     * conversion happens here rather than being left for the server to guess at.
     */
    private fun sign(data: ByteArray?): ByteArray {
        require(data != null && data.isNotEmpty()) { "There is nothing to sign." }

        val privateKey = loadPrivateKey()
            ?: throw IllegalStateException("No device key exists. Register first.")

        val der = Signature.getInstance("SHA256withECDSA").run {
            initSign(privateKey)
            update(data)
            sign()
        }

        return derToRaw(der)
    }

    private fun deleteKey(): Boolean {
        val keyStore = KeyStore.getInstance(KEYSTORE).apply { load(null) }

        if (!keyStore.containsAlias(KEY_ALIAS)) {
            return false
        }

        keyStore.deleteEntry(KEY_ALIAS)
        return true
    }

    private fun loadPrivateKey(): PrivateKey? =
        KeyStore.getInstance(KEYSTORE)
            .apply { load(null) }
            .let { it.getKey(KEY_ALIAS, null) as? PrivateKey }

    /** `0x04 ‖ X(32) ‖ Y(32)`, exactly as `core.Device.PublicKey` stores it. */
    private fun uncompressedPoint(publicKey: ECPublicKey): ByteArray {
        val point = ByteArray(1 + COORDINATE_BYTES * 2)
        point[0] = 0x04

        // BigInteger.toByteArray may carry a leading zero for sign, or be short
        // for a small coordinate. Both are copied right-aligned into a fixed
        // field rather than assumed to be exactly 32 bytes.
        writeFixed(publicKey.w.affineX, point, 1)
        writeFixed(publicKey.w.affineY, point, 1 + COORDINATE_BYTES)

        return point
    }

    private fun writeFixed(value: BigInteger, destination: ByteArray, offset: Int) {
        val bytes = value.toByteArray()
        val start = if (bytes.size > COORDINATE_BYTES) bytes.size - COORDINATE_BYTES else 0
        val length = minOf(bytes.size, COORDINATE_BYTES)

        System.arraycopy(bytes, start, destination, offset + COORDINATE_BYTES - length, length)
    }

    /**
     * Converts a DER `SEQUENCE { INTEGER r, INTEGER s }` into raw `r‖s`.
     *
     * Written by hand rather than pulled from a library: the transformation is
     * twenty lines, and adding a dependency to a security path costs more in
     * supply-chain surface than it saves (§47).
     */
    private fun derToRaw(der: ByteArray): ByteArray {
        var offset = 0

        require(der[offset++] == 0x30.toByte()) { "Signature is not a DER SEQUENCE." }

        // A sequence longer than 127 bytes uses the long form; an ECDSA P-256
        // signature never is, but the length byte is still skipped properly.
        if (der[offset].toInt() and 0x80 != 0) {
            offset += (der[offset].toInt() and 0x7F) + 1
        } else {
            offset++
        }

        val raw = ByteArray(COORDINATE_BYTES * 2)

        offset = readInteger(der, offset, raw, 0)
        readInteger(der, offset, raw, COORDINATE_BYTES)

        return raw
    }

    private fun readInteger(der: ByteArray, start: Int, destination: ByteArray, at: Int): Int {
        var offset = start

        require(der[offset++] == 0x02.toByte()) { "Expected a DER INTEGER." }

        val length = der[offset++].toInt()

        // DER encodes a leading zero when the high bit would otherwise make the
        // integer negative. That padding is not part of the value.
        val skip = if (length > COORDINATE_BYTES) length - COORDINATE_BYTES else 0
        val copied = length - skip

        System.arraycopy(der, offset + skip, destination, at + COORDINATE_BYTES - copied, copied)

        return offset + length
    }

    /** Wraps DER certificates in a `SEQUENCE OF`, leaf first. */
    private fun derSequenceOf(items: List<ByteArray>): ByteArray {
        val body = ByteArrayOutputStream().apply { items.forEach { write(it) } }.toByteArray()

        return ByteArrayOutputStream().apply {
            write(0x30)
            writeLength(body.size)
            write(body)
        }.toByteArray()
    }

    private fun ByteArrayOutputStream.writeLength(length: Int) {
        if (length < 0x80) {
            write(length)
            return
        }

        val bytes = ArrayList<Int>()
        var remaining = length

        while (remaining > 0) {
            bytes.add(0, remaining and 0xFF)
            remaining = remaining shr 8
        }

        write(0x80 or bytes.size)
        bytes.forEach { write(it) }
    }

    private fun errorCode(error: Exception): String = when (error) {
        is StrongBoxUnavailableException -> "STRONGBOX_UNAVAILABLE"
        is IllegalArgumentException -> "INVALID_ARGUMENT"
        is IllegalStateException -> "NO_KEY"
        else -> "KEYSTORE_ERROR"
    }
}
