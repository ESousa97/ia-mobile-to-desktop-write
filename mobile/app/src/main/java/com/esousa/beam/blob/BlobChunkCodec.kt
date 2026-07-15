package com.esousa.beam.blob

import com.esousa.beam.security.SessionCipher
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.UUID

object BlobChunkCodec {
    const val HEADER_SIZE = 24

    fun encodeChunk(blobId: ByteArray, sequence: Int, plaintext: ByteArray, cipher: SessionCipher): ByteArray {
        require(blobId.size == 16) { "O blobId deve ter 16 bytes." }
        val encrypted = cipher.encrypt(plaintext, associatedData(blobId, sequence))
        return ByteBuffer.allocate(HEADER_SIZE + encrypted.size).apply {
            put(blobId)
            putInt(sequence)
            putInt(encrypted.size)
            put(encrypted)
        }.array()
    }

    fun decodeChunk(frame: ByteArray, cipher: SessionCipher): DecodedChunk? {
        if (frame.size < HEADER_SIZE) return null
        val buffer = ByteBuffer.wrap(frame)
        val blobId = ByteArray(16).also { buffer.get(it) }
        val sequence = buffer.int
        val payloadLength = buffer.int
        if (payloadLength <= 0 || frame.size < HEADER_SIZE + payloadLength) return null
        val encrypted = frame.copyOfRange(HEADER_SIZE, HEADER_SIZE + payloadLength)
        return runCatching {
            DecodedChunk(
                blobId = blobId.toHex(),
                sequence = sequence,
                plaintext = cipher.decrypt(encrypted, associatedData(blobId, sequence)),
            )
        }.getOrNull()
    }

    fun createBlobIdBytes(): ByteArray = uuidToBytes(UUID.randomUUID())

    fun sha256Hex(data: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(data).toHex()

    private fun associatedData(blobId: ByteArray, sequence: Int): ByteArray =
        ByteBuffer.allocate(blobId.size + 4).apply {
            put(blobId)
            putInt(sequence)
        }.array()

    private fun uuidToBytes(uuid: UUID): ByteArray =
        ByteBuffer.allocate(16).apply {
            putLong(uuid.mostSignificantBits)
            putLong(uuid.leastSignificantBits)
        }.array()

    private fun ByteArray.toHex(): String = joinToString(separator = "") { byte ->
        "%02x".format(byte)
    }
}

data class DecodedChunk(val blobId: String, val sequence: Int, val plaintext: ByteArray)
