package com.esousa.beam.clipboard

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import java.nio.ByteBuffer
import java.security.MessageDigest

/**
 * Impressão digital de imagem: SHA-256 dos pixels RGB de uma miniatura 64×64.
 * Como o PNG é sem perdas e o redimensionamento é determinístico, a MESMA imagem
 * produz o MESMO hash mesmo após decodificar/recodificar — é o que permite detectar
 * "esta imagem já foi sincronizada" e cortar o loop infinito de eco, sem colidir
 * entre imagens diferentes.
 */
object ImageFingerprint {
    private const val SIZE = 64

    fun compute(pngBytes: ByteArray): Long? {
        val bitmap = BitmapFactory.decodeByteArray(pngBytes, 0, pngBytes.size) ?: return null
        return try {
            compute(bitmap)
        } finally {
            if (!bitmap.isRecycled) bitmap.recycle()
        }
    }

    fun compute(bitmap: Bitmap): Long {
        val small = Bitmap.createScaledBitmap(bitmap, SIZE, SIZE, true)
        try {
            val pixels = IntArray(SIZE * SIZE)
            small.getPixels(pixels, 0, SIZE, 0, 0, SIZE, SIZE)

            // Somente RGB — o alfa pode ser alterado por conversões intermediárias.
            val rgb = ByteBuffer.allocate(pixels.size * 3)
            pixels.forEach { pixel ->
                rgb.put(((pixel shr 16) and 0xFF).toByte())
                rgb.put(((pixel shr 8) and 0xFF).toByte())
                rgb.put((pixel and 0xFF).toByte())
            }

            val digest = MessageDigest.getInstance("SHA-256").digest(rgb.array())
            return ByteBuffer.wrap(digest, 0, 8).long
        } finally {
            if (small !== bitmap && !small.isRecycled) small.recycle()
        }
    }

    fun isSameImage(a: Long, b: Long): Boolean = a == b
}
