package com.esousa.beam.protocol

import com.esousa.beam.security.SessionCipher
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.encodeToJsonElement
import kotlinx.serialization.json.jsonObject
import java.util.Base64

/**
 * Cifra/decifra payloads no estado SECURE. Metadados (v, type, id, ts) ficam em claro;
 * o payload trafega como `{ "ct": "..." }`.
 */
object SecureEnvelope {
    private val cleartextTypes = setOf(
        MessageType.PAIR_REQUEST,
        MessageType.PAIR_RESPONSE,
        MessageType.PAIR_CONFIRM,
    )

    fun requiresEncryption(type: String): Boolean = type !in cleartextTypes

    fun encrypt(plaintext: Envelope, cipher: SessionCipher, json: Json): Envelope {
        if (!requiresEncryption(plaintext.type) || plaintext.payload == null) return plaintext

        val payloadJson = json.encodeToString(JsonObject.serializer(), plaintext.payload)
        val sealedBytes = cipher.encrypt(payloadJson.toByteArray(), associatedData(plaintext))
        return plaintext.copy(
            payload = json.encodeToJsonElement(EncryptedPayload(Base64.getEncoder().encodeToString(sealedBytes))).jsonObject,
        )
    }

    fun decrypt(wire: Envelope, cipher: SessionCipher, json: Json): Envelope {
        if (!requiresEncryption(wire.type)) return wire

        val encrypted = json.decodeFromJsonElement(EncryptedPayload.serializer(), requireNotNull(wire.payload))
        val payloadJson = String(
            cipher.decrypt(Base64.getDecoder().decode(encrypted.ct), associatedData(wire)),
        )
        return wire.copy(payload = json.decodeFromString<JsonObject>(payloadJson))
    }

    private fun associatedData(envelope: Envelope): ByteArray =
        "${envelope.type}|${envelope.id}".toByteArray()
}
