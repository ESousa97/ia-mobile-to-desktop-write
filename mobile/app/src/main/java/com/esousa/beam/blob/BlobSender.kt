package com.esousa.beam.blob

import com.esousa.beam.protocol.BlobBeginPayload
import com.esousa.beam.protocol.BlobEndPayload
import com.esousa.beam.protocol.Envelope
import com.esousa.beam.protocol.MessageType
import com.esousa.beam.protocol.Protocol
import com.esousa.beam.protocol.SecureEnvelope
import com.esousa.beam.security.SessionCipher
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.encodeToJsonElement
import kotlinx.serialization.json.jsonObject
import okhttp3.WebSocket

object BlobSender {
    fun send(
        socket: WebSocket,
        cipher: SessionCipher,
        json: Json,
        data: ByteArray,
        metadataFactory: (String) -> Envelope,
    ): String {
        val blobIdBytes = BlobChunkCodec.createBlobIdBytes()
        val blobId = blobIdBytes.toHex()
        val begin = Envelope(
            type = MessageType.BLOB_BEGIN,
            id = newId(),
            timestamp = System.currentTimeMillis(),
            payload = json.encodeToJsonElement(
                BlobBeginPayload(
                    blobId = blobId,
                    totalBytes = data.size.toLong(),
                    chunkSize = Protocol.DEFAULT_CHUNK_SIZE,
                    sha256 = BlobChunkCodec.sha256Hex(data),
                ),
            ).jsonObject,
        )
        sendEnvelope(socket, SecureEnvelope.encrypt(begin, cipher, json), json)

        var sequence = 0
        var offset = 0
        while (offset < data.size) {
            val length = minOf(Protocol.DEFAULT_CHUNK_SIZE, data.size - offset)
            val frame = BlobChunkCodec.encodeChunk(
                blobIdBytes,
                sequence,
                data.copyOfRange(offset, offset + length),
                cipher,
            )
            check(socket.send(okio.ByteString.of(*frame))) { "Falha ao enviar chunk." }
            offset += length
            sequence++
        }

        val end = Envelope(
            type = MessageType.BLOB_END,
            id = newId(),
            timestamp = System.currentTimeMillis(),
            payload = json.encodeToJsonElement(BlobEndPayload(blobId)).jsonObject,
        )
        sendEnvelope(socket, SecureEnvelope.encrypt(end, cipher, json), json)
        sendEnvelope(socket, SecureEnvelope.encrypt(metadataFactory(blobId), cipher, json), json)
        return blobId
    }

    private fun sendEnvelope(socket: WebSocket, envelope: Envelope, json: Json) {
        check(socket.send(json.encodeToString(Envelope.serializer(), envelope))) {
            "Falha ao enviar envelope."
        }
    }

    private fun newId(): String = java.util.UUID.randomUUID().toString().replace("-", "")

    private fun ByteArray.toHex(): String = joinToString(separator = "") { byte ->
        "%02x".format(byte)
    }
}
