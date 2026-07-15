package com.esousa.beam.security

import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Cifra de sessão AES-256-GCM, compatível no fio com o lado desktop.
 *
 * Formato do pacote: `nonce(12) | tag(16) | ciphertext(n)`.
 * Observação: o `Cipher` do Java produz `ciphertext || tag`; aqui reordenamos
 * para o mesmo layout do desktop (ver docs/SECURITY-DESIGN.md).
 */
class SessionCipher(private val key: ByteArray) {

    init {
        require(key.size == KEY_SIZE) { "A chave deve ter $KEY_SIZE bytes." }
    }

    fun encrypt(plaintext: ByteArray, associatedData: ByteArray? = null): ByteArray {
        val nonce = ByteArray(NONCE_SIZE).also { SecureRandom().nextBytes(it) }
        val cipher = Cipher.getInstance(TRANSFORMATION).apply {
            init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_BITS, nonce))
            associatedData?.let { updateAAD(it) }
        }

        val combined = cipher.doFinal(plaintext) // ciphertext || tag
        val ctLen = combined.size - TAG_SIZE
        val ciphertext = combined.copyOfRange(0, ctLen)
        val tag = combined.copyOfRange(ctLen, combined.size)

        return nonce + tag + ciphertext
    }

    fun decrypt(pkg: ByteArray, associatedData: ByteArray? = null): ByteArray {
        require(pkg.size >= NONCE_SIZE + TAG_SIZE) { "Pacote cifrado muito curto." }

        val nonce = pkg.copyOfRange(0, NONCE_SIZE)
        val tag = pkg.copyOfRange(NONCE_SIZE, NONCE_SIZE + TAG_SIZE)
        val ciphertext = pkg.copyOfRange(NONCE_SIZE + TAG_SIZE, pkg.size)

        val cipher = Cipher.getInstance(TRANSFORMATION).apply {
            init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_BITS, nonce))
            associatedData?.let { updateAAD(it) }
        }

        return cipher.doFinal(ciphertext + tag)
    }

    companion object {
        const val KEY_SIZE = 32   // AES-256
        const val NONCE_SIZE = 12 // 96 bits
        const val TAG_SIZE = 16   // 128 bits
        private const val TAG_BITS = 128
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
    }
}
