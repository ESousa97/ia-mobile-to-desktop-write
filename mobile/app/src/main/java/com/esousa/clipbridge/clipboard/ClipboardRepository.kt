package com.esousa.clipbridge.clipboard

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context

/**
 * Lê e escreve na área de transferência do Android. O conteúdo recebido do
 * desktop é gravado aqui; o conteúdo copiado no celular é lido para envio.
 */
class ClipboardRepository(context: Context) {

    private val clipboard =
        context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager

    fun readText(): String? =
        clipboard.primaryClip
            ?.takeIf { it.itemCount > 0 }
            ?.getItemAt(0)
            ?.coerceToText(null)
            ?.toString()

    fun writeText(text: String) {
        clipboard.setPrimaryClip(ClipData.newPlainText("ClipBridge", text))
    }

    fun addPrimaryClipChangedListener(listener: () -> Unit) {
        clipboard.addPrimaryClipChangedListener(listener)
    }
}
