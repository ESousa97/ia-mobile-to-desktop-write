package com.esousa.beam.security

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyStore
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import org.json.JSONObject

/**
 * Vínculo com um desktop já pareado. Enquanto [expiresAt] não vencer, o app
 * reconecta sozinho — sem pedir o código de seis dígitos de novo.
 */
data class TrustRecord(
    val host: String,
    val port: Int,
    val deviceId: String,
    val resumeKey: ByteArray,
    val expiresAt: Long,
) {
    val isValid: Boolean get() = System.currentTimeMillis() < expiresAt

    // resumeKey é ByteArray: equals/hashCode gerados comparariam referência.
    override fun equals(other: Any?): Boolean =
        this === other || (other is TrustRecord &&
            host == other.host && port == other.port && deviceId == other.deviceId &&
            resumeKey.contentEquals(other.resumeKey) && expiresAt == other.expiresAt)

    override fun hashCode(): Int =
        (((host.hashCode() * 31 + port) * 31 + deviceId.hashCode()) * 31 +
            resumeKey.contentHashCode()) * 31 + expiresAt.hashCode()
}

/**
 * Guarda o vínculo de retomada cifrado com uma chave do AndroidKeyStore.
 *
 * A chave de retomada permite reconectar sem código: em texto claro nas
 * SharedPreferences ela ficaria legível para qualquer backup ou app com root.
 * A chave AES vive no keystore do sistema (respaldada por hardware quando o
 * aparelho tem), então o blob só é decifrável neste dispositivo.
 */
class TrustStore(context: Context) {

    private val prefs = context.applicationContext
        .getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    /** Vínculo válido, ou null se não existe, está vencido ou não pôde ser lido. */
    fun load(): TrustRecord? {
        val stored = prefs.getString(KEY_RECORD, null) ?: return null
        val record = runCatching { decode(stored) }.getOrNull()
        if (record == null || !record.isValid) {
            clear()
            return null
        }
        return record
    }

    /** Cria ou renova o vínculo — a validade sempre conta a partir de agora. */
    fun save(host: String, port: Int, deviceId: String, resumeKey: ByteArray) {
        val record = TrustRecord(
            host = host,
            port = port,
            deviceId = deviceId,
            resumeKey = resumeKey,
            expiresAt = System.currentTimeMillis() + ResumeHandshake.TRUST_LIFETIME_MS,
        )
        runCatching { prefs.edit().putString(KEY_RECORD, encode(record)).apply() }
    }

    /** Atualiza o endereço do desktop sem mexer na validade (IP novo via DHCP). */
    fun updateEndpoint(host: String, port: Int) {
        val current = load() ?: return
        runCatching {
            prefs.edit()
                .putString(KEY_RECORD, encode(current.copy(host = host, port = port)))
                .apply()
        }
    }

    fun clear() {
        prefs.edit().remove(KEY_RECORD).apply()
    }

    private fun encode(record: TrustRecord): String {
        val json = JSONObject()
            .put("host", record.host)
            .put("port", record.port)
            .put("deviceId", record.deviceId)
            .put("resumeKey", Base64.getEncoder().encodeToString(record.resumeKey))
            .put("expiresAt", record.expiresAt)
            .toString()
            .toByteArray()

        val cipher = Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.ENCRYPT_MODE, secretKey()) }
        val payload = cipher.iv + cipher.doFinal(json)
        return Base64.getEncoder().encodeToString(payload)
    }

    private fun decode(stored: String): TrustRecord {
        val payload = Base64.getDecoder().decode(stored)
        val cipher = Cipher.getInstance(TRANSFORMATION).apply {
            init(
                Cipher.DECRYPT_MODE,
                secretKey(),
                GCMParameterSpec(TAG_BITS, payload, 0, IV_SIZE),
            )
        }

        val json = JSONObject(String(cipher.doFinal(payload, IV_SIZE, payload.size - IV_SIZE)))
        return TrustRecord(
            host = json.getString("host"),
            port = json.getInt("port"),
            deviceId = json.getString("deviceId"),
            resumeKey = Base64.getDecoder().decode(json.getString("resumeKey")),
            expiresAt = json.getLong("expiresAt"),
        )
    }

    private fun secretKey(): SecretKey {
        val keyStore = KeyStore.getInstance(KEYSTORE).apply { load(null) }
        (keyStore.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }

        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE).apply {
            init(
                KeyGenParameterSpec.Builder(
                    KEY_ALIAS,
                    KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
                )
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .build(),
            )
        }.generateKey()
    }

    private companion object {
        const val PREFS_NAME = "beam-trust"
        const val KEY_RECORD = "trust-record"
        const val KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "beam-trust-key"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_SIZE = 12
        const val TAG_BITS = 128
    }
}
