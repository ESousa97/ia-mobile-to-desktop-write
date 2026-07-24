package com.esousa.beam.security

import org.bouncycastle.crypto.digests.SHA256Digest
import org.bouncycastle.crypto.generators.HKDFBytesGenerator
import org.bouncycastle.crypto.params.HKDFParameters
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * Derivações do handshake de retomada — espelho de `Beam.Core.Security.ResumeHandshake`.
 *
 * No pareamento por código, os dois lados derivam do mesmo segredo ECDH uma
 * chave de retomada de longa duração (info HKDF distinta da chave de sessão) e a
 * persistem. Na reconexão, a chave de sessão sai de `HKDF(ECDH efêmero ‖ chave de
 * retomada)`: o ECDH novo preserva o sigilo futuro, a chave de retomada autentica
 * os dois lados. Qualquer divergência de bytes aqui quebra a interoperabilidade
 * com o desktop.
 */
object ResumeHandshake {

    /** Validade da confiança, renovada a cada conexão segura. */
    const val TRUST_LIFETIME_MS = 72L * 60 * 60 * 1000

    const val NONCE_SIZE = 32

    /** Info HKDF da chave de retomada, derivada durante o pareamento por código. */
    val RESUME_KEY_INFO = "clipbridge-v1-resume".toByteArray(StandardCharsets.UTF_8)

    private val ECDH_INFO = "clipbridge-v1-resume-ecdh".toByteArray(StandardCharsets.UTF_8)
    private val SESSION_INFO = "clipbridge-v1-resume-session".toByteArray(StandardCharsets.UTF_8)
    private val DEVICE_ID_INFO = "clipbridge-v1-device-id".toByteArray(StandardCharsets.UTF_8)
    private val SERVER_PROOF_LABEL = "clipbridge-v1-resume-server".toByteArray(StandardCharsets.UTF_8)
    private val CLIENT_PROOF_LABEL = "clipbridge-v1-resume-client".toByteArray(StandardCharsets.UTF_8)

    /** Info HKDF do ECDH efêmero da retomada. */
    val ecdhInfo: ByteArray get() = ECDH_INFO

    /**
     * Identificador público do vínculo: os dois lados chegam ao mesmo valor sem
     * trocá-lo, e ele não revela a chave de retomada.
     */
    fun computeDeviceId(resumeKey: ByteArray): String =
        hmac(resumeKey, DEVICE_ID_INFO).take(8).joinToString("") { "%02X".format(it) }

    /** Salt comum do handshake: os dois nonces, na ordem cliente‖servidor. */
    fun buildSalt(clientNonce: ByteArray, serverNonce: ByteArray): ByteArray =
        clientNonce + serverNonce

    /** Chave de sessão da retomada: ECDH efêmero misturado com a chave de retomada. */
    fun deriveSessionKey(ecdhKey: ByteArray, resumeKey: ByteArray, salt: ByteArray): ByteArray {
        val material = ecdhKey + resumeKey
        try {
            return ByteArray(SessionCipher.KEY_SIZE).also { sessionKey ->
                HKDFBytesGenerator(SHA256Digest()).apply {
                    init(HKDFParameters(material, salt, SESSION_INFO))
                    generateBytes(sessionKey, 0, sessionKey.size)
                }
            }
        } finally {
            material.fill(0)
        }
    }

    /** Prova de que o desktop conhece a chave de retomada. */
    fun serverProof(resumeKey: ByteArray, salt: ByteArray): ByteArray =
        hmac(resumeKey, SERVER_PROOF_LABEL + salt)

    /** Prova de que este celular conhece a chave de retomada. */
    fun clientProof(resumeKey: ByteArray, salt: ByteArray): ByteArray =
        hmac(resumeKey, CLIENT_PROOF_LABEL + salt)

    /** Comparação em tempo constante — nunca vaza quanto do prefixo bateu. */
    fun matches(a: ByteArray, b: ByteArray): Boolean = MessageDigest.isEqual(a, b)

    private fun hmac(key: ByteArray, message: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").run {
            init(SecretKeySpec(key, "HmacSHA256"))
            doFinal(message)
        }
}
