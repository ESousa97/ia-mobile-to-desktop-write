package com.esousa.beam.clipboard

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import androidx.core.content.FileProvider
import java.io.ByteArrayOutputStream
import java.io.File

/**
 * Lê e escreve na área de transferência do Android. O conteúdo recebido do
 * desktop é gravado aqui; o conteúdo copiado no celular é lido para envio.
 */
class ClipboardRepository(context: Context) {

    private val appContext = context.applicationContext
    private val clipboard =
        context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager

    private var listener: (() -> Unit)? = null

    fun readText(): String? =
        clipboard.primaryClip
            ?.takeIf { it.itemCount > 0 }
            ?.getItemAt(0)
            ?.coerceToText(appContext)
            ?.toString()
            ?.takeUnless { it.startsWith("content://") || it.startsWith("clipbridge:") }

    fun readImagePng(): ImageClip? {
        val clip = clipboard.primaryClip ?: return null
        for (index in 0 until clip.itemCount) {
            val item = clip.getItemAt(index)
            item.uri?.let { uri -> decodeUri(uri)?.let { return it } }
        }
        return null
    }

    fun writeText(text: String) {
        clipboard.setPrimaryClip(ClipData.newPlainText("ClipBridge", text))
    }

    fun writeImage(pngBytes: ByteArray, label: String = "ClipBridge") {
        val bitmap = BitmapFactory.decodeByteArray(pngBytes, 0, pngBytes.size) ?: return
        val cacheDir = File(appContext.cacheDir, "clipboard").apply { mkdirs() }
        val file = File(cacheDir, "clip-${System.currentTimeMillis()}.png")
        file.writeBytes(pngBytes)
        val uri = FileProvider.getUriForFile(appContext, "${appContext.packageName}.fileprovider", file)
        clipboard.setPrimaryClip(ClipData.newUri(appContext.contentResolver, label, uri))
        if (!bitmap.isRecycled) bitmap.recycle()
    }

    fun setOnPrimaryClipChangedListener(onChange: () -> Unit) {
        removePrimaryClipChangedListener()
        listener = onChange
        clipboard.addPrimaryClipChangedListener(onChange)
    }

    fun removePrimaryClipChangedListener() {
        listener?.let { registered ->
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                clipboard.removePrimaryClipChangedListener(registered)
            }
        }
        listener = null
    }

    private fun decodeUri(uri: Uri): ImageClip? = runCatching {
        appContext.contentResolver.openInputStream(uri)?.use { input ->
            val bitmap = BitmapFactory.decodeStream(input) ?: return null
            val clip = ImageClip(bitmap.toPngBytes(), bitmap.width, bitmap.height)
            if (!bitmap.isRecycled) bitmap.recycle()
            clip
        }
    }.getOrNull()

    private fun Bitmap.toPngBytes(): ByteArray = ByteArrayOutputStream().use { stream ->
        compress(Bitmap.CompressFormat.PNG, 100, stream)
        stream.toByteArray()
    }
}

data class ImageClip(val pngBytes: ByteArray, val width: Int, val height: Int)
