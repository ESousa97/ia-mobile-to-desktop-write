package com.esousa.beam.protocol

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject

/**
 * Modelos do protocolo ClipBridge (espelham o lado desktop — ver docs/PROTOCOL.md).
 */
object Protocol {
    const val VERSION = 1
    const val DEFAULT_PORT = 8787
    const val DISCOVERY_PORT = 8788
    const val ANNOUNCE_PORT = 8789
    const val DEFAULT_CHUNK_SIZE = 64 * 1024
    const val MAX_BLOB_BYTES = 50L * 1024 * 1024
    const val DISCOVERY_REQUEST = "clipbridge.discover.v1"
    const val ANNOUNCE_PREFIX = "clipbridge.announce.v1:"

    /**
     * Terminador do anúncio. Sem ele, um datagrama cortado no meio dos dígitos
     * (8787 → 878) passaria como porta válida e o app conectaria no lugar errado.
     */
    const val ANNOUNCE_SUFFIX = ";"
}

object MessageType {
    const val HELLO = "hello"
    const val PAIR_REQUEST = "pair.request"
    const val PAIR_RESPONSE = "pair.response"
    const val PAIR_CONFIRM = "pair.confirm"
    const val SESSION_RESUME = "session.resume"
    const val SESSION_RESUMED = "session.resumed"
    const val SESSION_RESUME_CONFIRM = "session.resume.confirm"
    const val CLIPBOARD_TEXT = "clipboard.text"
    const val CLIPBOARD_IMAGE = "clipboard.image"
    const val SCREENSHOT = "screenshot"
    const val BLOB_BEGIN = "blob.begin"
    const val BLOB_END = "blob.end"
    const val ACK = "ack"
    const val ERROR = "error"
    const val PING = "ping"
    const val PONG = "pong"
}

@Serializable
data class Envelope(
    @SerialName("v") val version: Int = Protocol.VERSION,
    @SerialName("type") val type: String,
    @SerialName("id") val id: String,
    @SerialName("ts") val timestamp: Long,
    @SerialName("payload") val payload: JsonObject? = null,
)

@Serializable
data class PairRequestPayload(
    val pubKey: String,
    val nonce: String,
)

@Serializable
data class PairResponsePayload(val pubKey: String)

@Serializable
data class PairConfirmPayload(val code: String)

@Serializable
data class SessionResumePayload(
    val deviceId: String,
    val pubKey: String,
    val nonce: String,
)

@Serializable
data class SessionResumedPayload(
    val pubKey: String,
    val nonce: String,
    val proof: String,
)

@Serializable
data class SessionResumeConfirmPayload(val proof: String)

@Serializable
data class AckPayload(val ackId: String)

@Serializable
data class ErrorPayload(val code: String, val message: String)

@Serializable
data class EncryptedPayload(val ct: String)

@Serializable
data class ClipboardTextPayload(
    val text: String,
    val mime: String = "text/plain; charset=utf-8",
)

@Serializable
data class ClipboardImagePayload(
    val blobId: String,
    val mime: String,
    val width: Int,
    val height: Int,
)

@Serializable
data class BlobBeginPayload(
    val blobId: String,
    val totalBytes: Long,
    val chunkSize: Int,
    val sha256: String,
)

@Serializable
data class BlobEndPayload(val blobId: String)

@Serializable
data class ScreenshotPayload(
    val blobId: String,
    val mime: String,
    val width: Int,
    val height: Int,
    val monitors: Int,
)
