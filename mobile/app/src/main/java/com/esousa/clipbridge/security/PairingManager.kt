package com.esousa.clipbridge.security

import com.esousa.clipbridge.protocol.PairConfirmPayload
import com.esousa.clipbridge.protocol.PairRequestPayload
import com.esousa.clipbridge.protocol.PairResponsePayload
import java.net.URI
import java.net.URLDecoder
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64

data class PairingInvitation(
    val host: String,
    val port: Int,
    val publicKey: String,
    val fingerprint: String,
    val token: String,
    val expiresAtMillis: Long,
) {
    val isExpired: Boolean
        get() = System.currentTimeMillis() >= expiresAtMillis

    companion object {
        fun parse(qrPayload: String): PairingInvitation {
            val uri = URI(qrPayload)
            require(uri.scheme == "clipbridge" && uri.host == "pair") { "QR Code do ClipBridge inválido." }
            val parameters = uri.rawQuery.orEmpty()
                .split('&')
                .filter { it.isNotEmpty() }
                .associate { entry ->
                    val (key, value) = entry.split('=', limit = 2).let { it[0] to it.getOrElse(1) { "" } }
                    URLDecoder.decode(key, StandardCharsets.UTF_8) to URLDecoder.decode(value, StandardCharsets.UTF_8)
                }

            val port = parameters.getValue("port").toInt()
            require(port in 1..65535) { "Porta de pareamento inválida." }
            val expiresAt = parameters.getValue("expiresAt").toLong()
            require(parameters.getValue("host").isNotBlank()) { "Host de pareamento ausente." }
            require(parameters.getValue("fingerprint").matches(Regex("[0-9A-F]{12}"))) { "Fingerprint inválido." }
            require(Base64.getDecoder().decode(parameters.getValue("pubKey")).size == X25519KeyAgreement.PUBLIC_KEY_SIZE) {
                "Chave pública do QR inválida."
            }

            return PairingInvitation(
                host = parameters.getValue("host"),
                port = port,
                publicKey = parameters.getValue("pubKey"),
                fingerprint = parameters.getValue("fingerprint"),
                token = parameters.getValue("token"),
                expiresAtMillis = expiresAt,
            )
        }
    }
}

class PairingManager {
    private var invitation: PairingInvitation? = null
    private var keyAgreement: X25519KeyAgreement? = null
    private var nonce: ByteArray? = null
    private var sessionKey: ByteArray? = null

    fun createRequest(pairingInvitation: PairingInvitation): PairRequestPayload {
        check(!pairingInvitation.isExpired) { "Este QR Code de pareamento expirou." }
        cancel()
        invitation = pairingInvitation
        keyAgreement = X25519KeyAgreement()
        nonce = ByteArray(NONCE_SIZE).also(SecureRandom()::nextBytes)
        return PairRequestPayload(
            pubKey = Base64.getEncoder().encodeToString(requireNotNull(keyAgreement).publicKey),
            nonce = Base64.getEncoder().encodeToString(requireNotNull(nonce)),
        )
    }

    fun createConfirmation(response: PairResponsePayload): PairConfirmPayload {
        val activeInvitation = requireNotNull(invitation) { "Não há pareamento em andamento." }
        check(!activeInvitation.isExpired) { "Este QR Code de pareamento expirou." }
        check(MessageDigest.isEqual(response.fingerprint.toByteArray(), activeInvitation.fingerprint.toByteArray())) {
            "O fingerprint recebido não confere com o QR Code."
        }

        val remoteKey = Base64.getDecoder().decode(response.pubKey)
        sessionKey = requireNotNull(keyAgreement).deriveSessionKey(
            remoteKey,
            requireNotNull(nonce),
            SESSION_KEY_INFO,
        )
        return PairConfirmPayload(activeInvitation.token)
    }

    fun complete(): SessionCipher {
        val key = requireNotNull(sessionKey) { "O handshake ainda não derivou uma chave de sessão." }
        return SessionCipher(key.copyOf()).also { cancel() }
    }

    fun cancel() {
        nonce?.fill(0)
        sessionKey?.fill(0)
        invitation = null
        keyAgreement = null
        nonce = null
        sessionKey = null
    }

    companion object {
        private const val NONCE_SIZE = 32
        private val SESSION_KEY_INFO = "clipbridge-v1-session".toByteArray(StandardCharsets.UTF_8)
    }
}