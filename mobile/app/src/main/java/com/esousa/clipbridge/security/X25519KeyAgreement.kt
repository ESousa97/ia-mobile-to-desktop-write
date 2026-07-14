package com.esousa.clipbridge.security

import org.bouncycastle.crypto.agreement.X25519Agreement
import org.bouncycastle.crypto.digests.SHA256Digest
import org.bouncycastle.crypto.generators.HKDFBytesGenerator
import org.bouncycastle.crypto.params.HKDFParameters
import org.bouncycastle.crypto.params.X25519PrivateKeyParameters
import org.bouncycastle.crypto.params.X25519PublicKeyParameters
import org.bouncycastle.crypto.prng.FixedSecureRandom
import java.security.MessageDigest
import java.security.SecureRandom

/** X25519 com HKDF-SHA256, compatível com a implementação do desktop. */
class X25519KeyAgreement {
    private val privateKey = X25519PrivateKeyParameters(SecureRandom())

    val publicKey: ByteArray = privateKey.generatePublicKey().encoded
    val fingerprint: String
        get() = MessageDigest.getInstance("SHA-256")
            .digest(publicKey)
            .take(6)
            .joinToString("") { "%02X".format(it) }

    fun deriveSessionKey(peerPublicKey: ByteArray, salt: ByteArray, info: ByteArray): ByteArray {
        require(peerPublicKey.size == PUBLIC_KEY_SIZE) { "A chave pública X25519 deve ter $PUBLIC_KEY_SIZE bytes." }

        val sharedSecret = ByteArray(X25519PrivateKeyParameters.SECRET_SIZE)
        try {
            val agreement = X25519Agreement()
            agreement.init(privateKey)
            agreement.calculateAgreement(X25519PublicKeyParameters(peerPublicKey, 0), sharedSecret, 0)

            return ByteArray(SessionCipher.KEY_SIZE).also { sessionKey ->
                HKDFBytesGenerator(SHA256Digest()).apply {
                    init(HKDFParameters(sharedSecret, salt, info))
                    generateBytes(sessionKey, 0, sessionKey.size)
                }
            }
        } finally {
            sharedSecret.fill(0)
        }
    }

    companion object {
        const val PUBLIC_KEY_SIZE = 32
    }
}