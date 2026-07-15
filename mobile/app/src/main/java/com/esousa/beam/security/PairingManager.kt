package com.esousa.beam.security

import com.esousa.beam.protocol.PairConfirmPayload
import com.esousa.beam.protocol.PairRequestPayload
import com.esousa.beam.protocol.PairResponsePayload
import java.nio.charset.StandardCharsets
import java.security.SecureRandom
import java.util.Base64

class PairingManager {
    private var keyAgreement: X25519KeyAgreement? = null
    private var nonce: ByteArray? = null
    private var sessionKey: ByteArray? = null

    fun createRequest(): PairRequestPayload {
        cancel()
        keyAgreement = X25519KeyAgreement()
        nonce = ByteArray(NONCE_SIZE).also(SecureRandom()::nextBytes)
        return PairRequestPayload(
            pubKey = Base64.getEncoder().encodeToString(requireNotNull(keyAgreement).publicKey),
            nonce = Base64.getEncoder().encodeToString(requireNotNull(nonce)),
        )
    }

    fun createConfirmation(response: PairResponsePayload, code: String): PairConfirmPayload {
        require(code.matches(Regex("\\d{6}"))) { "O código de autenticação deve ter 6 dígitos." }

        val remoteKey = Base64.getDecoder().decode(response.pubKey)
        sessionKey = requireNotNull(keyAgreement).deriveSessionKey(
            remoteKey,
            requireNotNull(nonce),
            SESSION_KEY_INFO,
        )
        return PairConfirmPayload(code)
    }

    fun complete(): SessionCipher {
        val key = requireNotNull(sessionKey) { "O handshake ainda não derivou uma chave de sessão." }
        return SessionCipher(key.copyOf()).also { cancel() }
    }

    fun cancel() {
        nonce?.fill(0)
        sessionKey?.fill(0)
        keyAgreement = null
        nonce = null
        sessionKey = null
    }

    companion object {
        private const val NONCE_SIZE = 32
        private val SESSION_KEY_INFO = "clipbridge-v1-session".toByteArray(StandardCharsets.UTF_8)
    }
}