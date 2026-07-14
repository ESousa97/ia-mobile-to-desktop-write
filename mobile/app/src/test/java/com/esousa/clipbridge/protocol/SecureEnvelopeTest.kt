package com.esousa.clipbridge.protocol

import com.esousa.clipbridge.security.SessionCipher
import com.esousa.clipbridge.security.X25519KeyAgreement
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.encodeToJsonElement
import kotlinx.serialization.json.jsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Test
import java.util.Base64
import java.util.UUID

class SecureEnvelopeTest {
    private val json = Json { ignoreUnknownKeys = true }

    @Test
    fun encryptDecrypt_roundTripsClipboardPayload() {
        val cipher = SessionCipher(ByteArray(SessionCipher.KEY_SIZE))
        val original = envelope(MessageType.CLIPBOARD_TEXT, ClipboardTextPayload("conteúdo secreto"))

        val wire = SecureEnvelope.encrypt(original, cipher, json)
        val decrypted = SecureEnvelope.decrypt(wire, cipher, json)

        assertEquals(MessageType.CLIPBOARD_TEXT, decrypted.type)
        assertEquals(original.id, decrypted.id)
        assertEquals(
            "conteúdo secreto",
            json.decodeFromJsonElement(ClipboardTextPayload.serializer(), requireNotNull(decrypted.payload)).text,
        )
    }

    @Test
    fun pairRequest_staysCleartext() {
        val cipher = SessionCipher(ByteArray(SessionCipher.KEY_SIZE))
        val mobile = X25519KeyAgreement()
        val request = envelope(
            MessageType.PAIR_REQUEST,
            PairRequestPayload(
                pubKey = Base64.getEncoder().encodeToString(mobile.publicKey),
                nonce = Base64.getEncoder().encodeToString(ByteArray(32)),
            ),
        )

        val wire = SecureEnvelope.encrypt(request, cipher, json)

        assertNotNull(json.decodeFromJsonElement(PairRequestPayload.serializer(), requireNotNull(wire.payload)))
        assertNotNull(
            json.decodeFromJsonElement(
                PairRequestPayload.serializer(),
                requireNotNull(SecureEnvelope.decrypt(wire, cipher, json).payload),
            ),
        )
    }

    private inline fun <reified T> envelope(type: String, payload: T): Envelope = Envelope(
        type = type,
        id = UUID.randomUUID().toString().replace("-", ""),
        timestamp = System.currentTimeMillis(),
        payload = json.encodeToJsonElement(payload).jsonObject,
    )
}
