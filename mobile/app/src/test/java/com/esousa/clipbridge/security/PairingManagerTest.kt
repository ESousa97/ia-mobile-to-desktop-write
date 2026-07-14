package com.esousa.clipbridge.security

import com.esousa.clipbridge.protocol.PairResponsePayload
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test
import java.security.SecureRandom
import java.util.Base64

class PairingManagerTest {
    @Test
    fun pairingRequestAndResponse_deriveTheSameSessionKey() {
        val desktop = X25519KeyAgreement()
        val manager = PairingManager()
        val invitation = PairingInvitation(
            host = "192.168.1.20",
            port = 8787,
            publicKey = Base64.getEncoder().encodeToString(desktop.publicKey),
            fingerprint = desktop.fingerprint,
            token = Base64.getEncoder().encodeToString(ByteArray(32).also(SecureRandom()::nextBytes)),
            expiresAtMillis = System.currentTimeMillis() + 60_000,
        )

        val request = manager.createRequest(invitation)
        val mobilePublicKey = Base64.getDecoder().decode(request.pubKey)
        val nonce = Base64.getDecoder().decode(request.nonce)
        val desktopKey = desktop.deriveSessionKey(mobilePublicKey, nonce, "clipbridge-v1-session".toByteArray())
        val confirmation = manager.createConfirmation(
            PairResponsePayload(
                pubKey = Base64.getEncoder().encodeToString(desktop.publicKey),
                fingerprint = desktop.fingerprint,
            ),
        )
        val mobileCipher = manager.complete()
        val desktopCipher = SessionCipher(desktopKey)
        val packageFromDesktop = desktopCipher.encrypt("sessão segura".toByteArray())

        assertEquals(invitation.token, confirmation.token)
        assertArrayEquals("sessão segura".toByteArray(), mobileCipher.decrypt(packageFromDesktop))
    }

    @Test(expected = IllegalStateException::class)
    fun createConfirmation_rejectsFingerprintDifferentFromQrCode() {
        val desktop = X25519KeyAgreement()
        val manager = PairingManager()
        val invitation = PairingInvitation(
            host = "desktop.local",
            port = 8787,
            publicKey = Base64.getEncoder().encodeToString(desktop.publicKey),
            fingerprint = "001122334455",
            token = Base64.getEncoder().encodeToString(ByteArray(32).also(SecureRandom()::nextBytes)),
            expiresAtMillis = System.currentTimeMillis() + 60_000,
        )

        manager.createRequest(invitation)
        manager.createConfirmation(
            PairResponsePayload(
                pubKey = Base64.getEncoder().encodeToString(desktop.publicKey),
                fingerprint = desktop.fingerprint,
            ),
        )
    }
}