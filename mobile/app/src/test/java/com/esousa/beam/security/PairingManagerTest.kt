package com.esousa.beam.security

import com.esousa.beam.protocol.PairResponsePayload
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test
import java.util.Base64

class PairingManagerTest {
    @Test
    fun pairingRequestAndResponse_deriveTheSameSessionKey() {
        val desktop = X25519KeyAgreement()
        val manager = PairingManager()

        val request = manager.createRequest()
        val mobilePublicKey = Base64.getDecoder().decode(request.pubKey)
        val nonce = Base64.getDecoder().decode(request.nonce)
        val desktopKey = desktop.deriveSessionKey(mobilePublicKey, nonce, "clipbridge-v1-session".toByteArray())
        val confirmation = manager.createConfirmation(
            PairResponsePayload(
                pubKey = Base64.getEncoder().encodeToString(desktop.publicKey),
            ),
            code = "123456",
        )
        val mobileCipher = manager.complete()
        val desktopCipher = SessionCipher(desktopKey)
        val packageFromDesktop = desktopCipher.encrypt("sessão segura".toByteArray())

        assertEquals("123456", confirmation.code)
        assertArrayEquals("sessão segura".toByteArray(), mobileCipher.decrypt(packageFromDesktop))
    }

    @Test(expected = IllegalArgumentException::class)
    fun createConfirmation_rejectsMalformedCode() {
        val desktop = X25519KeyAgreement()
        val manager = PairingManager()

        manager.createRequest()
        manager.createConfirmation(
            PairResponsePayload(
                pubKey = Base64.getEncoder().encodeToString(desktop.publicKey),
            ),
            code = "12ab56",
        )
    }
}
