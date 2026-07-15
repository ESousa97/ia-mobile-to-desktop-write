package com.esousa.beam.blob

import com.esousa.beam.protocol.BlobBeginPayload
import com.esousa.beam.protocol.BlobEndPayload
import com.esousa.beam.protocol.Protocol

class BlobReceiver {
    private val pending = mutableMapOf<String, PendingBlob>()

    fun begin(begin: BlobBeginPayload): Boolean {
        if (begin.totalBytes <= 0 || begin.totalBytes > Protocol.MAX_BLOB_BYTES) return false
        pending[begin.blobId.lowercase()] = PendingBlob(begin.totalBytes, begin.chunkSize, begin.sha256)
        return true
    }

    fun addChunk(blobId: String, sequence: Int, chunk: ByteArray): Boolean =
        pending[blobId.lowercase()]?.write(sequence, chunk) == true

    fun complete(end: BlobEndPayload): ByteArray? {
        val pendingBlob = pending.remove(end.blobId.lowercase()) ?: return null
        if (!pendingBlob.isComplete) return null
        val data = pendingBlob.toByteArray()
        return if (BlobChunkCodec.sha256Hex(data).equals(pendingBlob.expectedSha256, ignoreCase = true)) {
            data
        } else {
            null
        }
    }

    private class PendingBlob(totalBytes: Long, chunkSize: Int, val expectedSha256: String) {
        private val buffer = ByteArray(totalBytes.toInt())
        private val chunkSize = chunkSize
        private val receivedChunks = BooleanArray(((totalBytes + chunkSize - 1) / chunkSize).toInt())
        private var receivedBytes = 0

        val isComplete: Boolean
            get() = receivedBytes == buffer.size

        fun write(sequence: Int, chunk: ByteArray): Boolean {
            if (sequence !in receivedChunks.indices || receivedChunks[sequence]) return false
            val offset = sequence * chunkSize
            if (offset + chunk.size > buffer.size) return false
            chunk.copyInto(buffer, offset)
            receivedChunks[sequence] = true
            receivedBytes += chunk.size
            return true
        }

        fun toByteArray(): ByteArray = buffer
    }
}
