package com.esousa.beam.service

import android.content.ClipboardManager
import android.content.ClipDescription
import android.content.ComponentName
import android.content.Context
import android.content.res.ColorStateList
import android.content.res.Configuration
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.inputmethodservice.InputMethodService
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import android.util.TypedValue
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.view.inputmethod.InputContentInfo
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import com.esousa.beam.ClipBridgeApplication
import java.util.Locale
import kotlin.math.roundToInt

/**
 * Teclado padrão do Beam. Como IME selecionado pelo usuário, o Android permite
 * observar o clipboard mesmo com o app fora de foco.
 *
 * Visual inspirado no Samsung Keyboard (One UI): temas claro/escuro seguindo o
 * sistema, dicas numéricas na fileira superior (segure para inserir), tecla
 * "!#1" com páginas de símbolos 1/2 e 2/2, barra de espaço sem rótulo,
 * shift com caps lock por toque duplo e backspace com repetição.
 */
class BeamInputMethodService : InputMethodService() {

    private lateinit var clipboard: ClipboardManager
    private var shifted = false
    private var capsLock = false
    private var lastShiftTapMs = 0L
    private var keyboardPage = KeyboardPage.Letters
    private var keyboard: LinearLayout? = null
    private var clipboardTray: LinearLayout? = null
    private var alternativesTray: LinearLayout? = null
    private var palette = LightPalette

    private val backspaceRepeater = Handler(Looper.getMainLooper())
    private val backspaceRepeatRunnable = object : Runnable {
        override fun run() {
            currentInputConnection?.deleteSurroundingText(1, 0)
            backspaceRepeater.postDelayed(this, BackspaceRepeatIntervalMs)
        }
    }

    private val clipboardListener = ClipboardManager.OnPrimaryClipChangedListener {
        (application as? ClipBridgeApplication)?.session?.syncCopiedTextFromInputMethod()
        clipboardTray?.post(::refreshClipboardImage)
    }

    override fun onCreate() {
        super.onCreate()
        clipboard = getSystemService(ClipboardManager::class.java)
        clipboard.addPrimaryClipChangedListener(clipboardListener)
    }

    override fun onDestroy() {
        backspaceRepeater.removeCallbacksAndMessages(null)
        if (::clipboard.isInitialized) {
            clipboard.removePrimaryClipChangedListener(clipboardListener)
        }
        super.onDestroy()
    }

    override fun onCreateInputView(): View {
        palette = if (isNightMode()) DarkPalette else LightPalette
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(palette.surface)
            setPadding(6.dp, 8.dp, 6.dp, 10.dp)
            clipChildren = false
            clipToPadding = false
            clipboardTray = createClipboardTray().also(::addView)
            alternativesTray = createAlternativesTray().also(::addView)
            keyboard = LinearLayout(this@BeamInputMethodService).apply {
                orientation = LinearLayout.VERTICAL
                clipChildren = false
                clipToPadding = false
            }.also(::addView)
            renderKeyboard()
            refreshClipboardImage()
        }
    }

    override fun onStartInputView(info: EditorInfo?, restarting: Boolean) {
        super.onStartInputView(info, restarting)
        hideAlternatives()
        keyboardPage = KeyboardPage.Letters
        capsLock = false
        shifted = shouldAutoShift()
        renderKeyboard()
        refreshClipboardImage()
    }

    override fun onFinishInputView(finishingInput: Boolean) {
        stopBackspaceRepeat()
        super.onFinishInputView(finishingInput)
    }

    override fun onUpdateSelection(
        oldSelStart: Int,
        oldSelEnd: Int,
        newSelStart: Int,
        newSelEnd: Int,
        candidatesStart: Int,
        candidatesEnd: Int,
    ) {
        super.onUpdateSelection(oldSelStart, oldSelEnd, newSelStart, newSelEnd, candidatesStart, candidatesEnd)
        if (!capsLock && keyboardPage == KeyboardPage.Letters) {
            val autoShift = shouldAutoShift()
            if (autoShift != shifted) {
                shifted = autoShift
                renderKeyboard()
            }
        }
    }

    private fun isNightMode(): Boolean =
        resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK == Configuration.UI_MODE_NIGHT_YES

    private fun shouldAutoShift(): Boolean {
        val editorInfo = currentInputEditorInfo ?: return false
        if (editorInfo.inputType == 0) return false
        val inputConnection = currentInputConnection ?: return false
        return inputConnection.getCursorCapsMode(editorInfo.inputType) != 0
    }

    private fun renderKeyboard() {
        val container = keyboard ?: return
        container.removeAllViews()
        val rows = when (keyboardPage) {
            KeyboardPage.Letters -> letterRows()
            KeyboardPage.Symbols1 -> symbols1Rows()
            KeyboardPage.Symbols2 -> symbols2Rows()
        }
        rows.forEach { row -> container.addView(createRow(row)) }
    }

    private fun createRow(row: Row): LinearLayout = LinearLayout(this).apply {
        gravity = Gravity.CENTER
        orientation = LinearLayout.HORIZONTAL
        clipChildren = false
        clipToPadding = false
        layoutParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            LinearLayout.LayoutParams.WRAP_CONTENT,
        )
        if (row.sideSpacing > 0f) addView(createSpacer(row.sideSpacing))
        row.keys.forEach { key -> addView(createKey(key)) }
        if (row.sideSpacing > 0f) addView(createSpacer(row.sideSpacing))
    }

    private fun createSpacer(weight: Float): View = View(this).apply {
        layoutParams = LinearLayout.LayoutParams(0, 0, weight)
    }

    private fun createClipboardTray(): LinearLayout = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        visibility = View.GONE
        layoutParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            LinearLayout.LayoutParams.WRAP_CONTENT,
        ).apply {
            bottomMargin = 4.dp
        }
    }

    private fun createAlternativesTray(): LinearLayout = LinearLayout(this).apply {
        gravity = Gravity.CENTER
        orientation = LinearLayout.HORIZONTAL
        visibility = View.GONE
        layoutParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            LinearLayout.LayoutParams.WRAP_CONTENT,
        ).apply {
            bottomMargin = 4.dp
        }
    }

    private fun refreshClipboardImage() {
        val tray = clipboardTray ?: return
        val image = readClipboardImage()
        tray.removeAllViews()
        if (image == null) {
            tray.visibility = View.GONE
            return
        }

        tray.visibility = View.VISIBLE
        tray.addView(createImagePasteAction(image))
    }

    private fun createImagePasteAction(image: ClipboardImage): LinearLayout = LinearLayout(this).apply {
        gravity = Gravity.CENTER_VERTICAL
        setPadding(6.dp, 6.dp, 10.dp, 6.dp)
        background = createTrayBackground()
        contentDescription = "Inserir imagem copiada"
        isClickable = true
        isFocusable = true
        layoutParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            60.dp,
        )

        addView(ImageView(this@BeamInputMethodService).apply {
            contentDescription = "Prévia da imagem copiada"
            scaleType = ImageView.ScaleType.CENTER_CROP
            setImageBitmap(loadThumbnail(image.uri))
            background = createPreviewBackground()
            clipToOutline = true
            layoutParams = LinearLayout.LayoutParams(48.dp, 48.dp)
        })

        addView(TextView(this@BeamInputMethodService).apply {
            text = "Imagem copiada"
            setTextColor(palette.trayText)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            gravity = Gravity.CENTER_VERTICAL
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, 1f).apply {
                marginStart = 10.dp
            }
        })

        addView(TextView(this@BeamInputMethodService).apply {
            text = "Inserir"
            setTextColor(palette.accent)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            gravity = Gravity.CENTER
            layoutParams = LinearLayout.LayoutParams(64.dp, LinearLayout.LayoutParams.MATCH_PARENT)
        })

        setOnClickListener { pasteClipboardImage(image) }
    }

    private fun createKey(key: Key): View {
        val enterAction = if (key.command == Command.Enter) resolveEnterAction() else null
        val accented = (key.command == Command.Shift && (shifted || capsLock)) || enterAction != null
        val label = when {
            key.command == Command.Shift && capsLock -> "⇪"
            enterAction != null -> enterAction.label
            else -> key.label
        }

        val container = FrameLayout(this).apply {
            contentDescription = key.contentDescription
            isClickable = true
            isFocusable = true
            background = createKeyBackground(key, accented)
            if (palette.raisedKeys) elevation = 1.5f.dp
            layoutParams = LinearLayout.LayoutParams(0, KeyHeightDp.dp, key.weight).apply {
                setMargins(3.dp, 4.dp, 3.dp, 4.dp)
            }
        }

        val maximumTextSize = when {
            key.command != null || enterAction != null -> if (label.length == 1) 20 else 14
            label.length == 1 -> 21
            else -> 14
        }

        container.addView(TextView(this).apply {
            text = label
            gravity = Gravity.CENTER
            setSingleLine()
            includeFontPadding = false
            setTextColor(if (accented) palette.onAccent else palette.keyText)
            typeface = Typeface.create(
                if (key.command != null) "sans-serif-medium" else "sans-serif",
                Typeface.NORMAL,
            )
            setTextSize(TypedValue.COMPLEX_UNIT_SP, maximumTextSize.toFloat())
            setAutoSizeTextTypeUniformWithConfiguration(
                10,
                maximumTextSize,
                1,
                TypedValue.COMPLEX_UNIT_SP,
            )
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT,
            )
        })

        key.hint?.let { hint ->
            container.addView(TextView(this).apply {
                text = hint
                setSingleLine()
                includeFontPadding = false
                setTextColor(if (accented) palette.onAccent else palette.hintText)
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f)
                layoutParams = FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                    Gravity.TOP or Gravity.END,
                ).apply {
                    topMargin = 3.dp
                    marginEnd = 6.dp
                }
            })
        }

        container.setOnClickListener { view ->
            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
            handleKey(key)
        }
        when {
            key.command == Command.Backspace -> {
                container.setOnLongClickListener { view ->
                    view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                    startBackspaceRepeat()
                    true
                }
                container.setOnTouchListener { _, event ->
                    if (event.actionMasked == MotionEvent.ACTION_UP ||
                        event.actionMasked == MotionEvent.ACTION_CANCEL
                    ) {
                        stopBackspaceRepeat()
                    }
                    false
                }
            }

            !key.alternatives.isNullOrEmpty() -> container.setOnLongClickListener { view ->
                view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                showAlternatives(key.alternatives)
                true
            }
        }
        return container
    }

    private fun handleKey(key: Key) {
        hideAlternatives()
        when (key.command) {
            Command.Shift -> {
                val now = SystemClock.uptimeMillis()
                when {
                    capsLock -> {
                        capsLock = false
                        shifted = false
                    }

                    shifted && now - lastShiftTapMs < DoubleTapShiftWindowMs -> capsLock = true

                    else -> shifted = !shifted
                }
                lastShiftTapMs = now
                renderKeyboard()
            }

            Command.Backspace -> currentInputConnection?.deleteSurroundingText(1, 0)

            Command.Symbols1 -> {
                keyboardPage = KeyboardPage.Symbols1
                shifted = false
                capsLock = false
                renderKeyboard()
            }

            Command.Symbols2 -> {
                keyboardPage = KeyboardPage.Symbols2
                renderKeyboard()
            }

            Command.Letters -> {
                keyboardPage = KeyboardPage.Letters
                shifted = shouldAutoShift()
                renderKeyboard()
            }

            Command.Enter -> submitEnter()

            null -> key.text?.let { value ->
                currentInputConnection?.commitText(value, 1)
                if (shifted && !capsLock && keyboardPage == KeyboardPage.Letters) {
                    shifted = false
                    renderKeyboard()
                }
            }
        }
    }

    private fun submitEnter() {
        val inputConnection = currentInputConnection ?: return
        val action = resolveEnterAction()
        if (action != null) {
            inputConnection.performEditorAction(action.imeAction)
        } else {
            inputConnection.sendKeyEvent(KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_ENTER))
            inputConnection.sendKeyEvent(KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_ENTER))
        }
    }

    /**
     * Como no teclado da Samsung, o Enter vira uma tecla de ação destacada em
     * azul quando o campo define uma ação (buscar, enviar, concluir...).
     */
    private fun resolveEnterAction(): EnterAction? {
        val options = currentInputEditorInfo?.imeOptions ?: return null
        if (options and EditorInfo.IME_FLAG_NO_ENTER_ACTION != 0) return null
        return when (options and EditorInfo.IME_MASK_ACTION) {
            EditorInfo.IME_ACTION_GO -> EnterAction("→", EditorInfo.IME_ACTION_GO)
            EditorInfo.IME_ACTION_NEXT -> EnterAction("→", EditorInfo.IME_ACTION_NEXT)
            EditorInfo.IME_ACTION_SEARCH -> EnterAction("→", EditorInfo.IME_ACTION_SEARCH)
            EditorInfo.IME_ACTION_SEND -> EnterAction("➤", EditorInfo.IME_ACTION_SEND)
            EditorInfo.IME_ACTION_DONE -> EnterAction("✓", EditorInfo.IME_ACTION_DONE)
            EditorInfo.IME_ACTION_PREVIOUS -> EnterAction("←", EditorInfo.IME_ACTION_PREVIOUS)
            else -> null
        }
    }

    private fun startBackspaceRepeat() {
        stopBackspaceRepeat()
        backspaceRepeater.post(backspaceRepeatRunnable)
    }

    private fun stopBackspaceRepeat() {
        backspaceRepeater.removeCallbacks(backspaceRepeatRunnable)
    }

    private fun showAlternatives(alternatives: String) {
        val tray = alternativesTray ?: return
        tray.removeAllViews()
        alternatives.forEach { alternative ->
            tray.addView(createAlternativeKey(alternative.toString()))
        }
        tray.visibility = View.VISIBLE
    }

    private fun createAlternativeKey(value: String): TextView = TextView(this).apply {
        text = value
        contentDescription = "Inserir $value"
        gravity = Gravity.CENTER
        setSingleLine()
        setTextColor(palette.keyText)
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 20f)
        typeface = Typeface.create("sans-serif", Typeface.NORMAL)
        background = createAlternativeBackground()
        isClickable = true
        isFocusable = true
        layoutParams = LinearLayout.LayoutParams(0, 44.dp, 1f).apply {
            setMargins(2.dp, 0, 2.dp, 0)
        }
        setOnClickListener { view ->
            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
            currentInputConnection?.commitText(value, 1)
            hideAlternatives()
            if (shifted && !capsLock && keyboardPage == KeyboardPage.Letters) {
                shifted = false
                renderKeyboard()
            }
        }
    }

    private fun hideAlternatives() {
        alternativesTray?.apply {
            removeAllViews()
            visibility = View.GONE
        }
    }

    private fun createKeyBackground(key: Key, accented: Boolean): RippleDrawable {
        val fillColor = when {
            accented -> palette.accent
            key.command != null || key.function -> palette.functionKey
            else -> palette.letterKey
        }
        val shape = GradientDrawable().apply {
            shape = GradientDrawable.RECTANGLE
            cornerRadius = KeyCornerRadiusDp.dp.toFloat()
            setColor(fillColor)
        }
        return RippleDrawable(ColorStateList.valueOf(palette.pressed), shape, null)
    }

    private fun createTrayBackground(): RippleDrawable {
        val shape = GradientDrawable().apply {
            shape = GradientDrawable.RECTANGLE
            cornerRadius = KeyCornerRadiusDp.dp.toFloat()
            setColor(palette.tray)
        }
        return RippleDrawable(ColorStateList.valueOf(palette.pressed), shape, null)
    }

    private fun createAlternativeBackground(): RippleDrawable {
        val shape = GradientDrawable().apply {
            shape = GradientDrawable.RECTANGLE
            cornerRadius = 10.dp.toFloat()
            setColor(palette.letterKey)
        }
        return RippleDrawable(ColorStateList.valueOf(palette.pressed), shape, null)
    }

    private fun createPreviewBackground(): GradientDrawable = GradientDrawable().apply {
        shape = GradientDrawable.RECTANGLE
        cornerRadius = 6.dp.toFloat()
        setColor(palette.previewFallback)
    }

    private fun readClipboardImage(): ClipboardImage? {
        val clip = clipboard.primaryClip?.takeIf { it.itemCount > 0 } ?: return null
        val uri = clip.getItemAt(0).uri ?: return null
        val mimeType = imageMimeType(uri, clip.description) ?: return null
        return ClipboardImage(uri, mimeType)
    }

    private fun loadThumbnail(uri: Uri): Bitmap? = runCatching {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        contentResolver.openInputStream(uri)?.use { input ->
            BitmapFactory.decodeStream(input, null, bounds)
        }
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) {
            return@runCatching null
        }

        val targetSize = 96.dp
        var sampleSize = 1
        while (bounds.outWidth / sampleSize > targetSize * 2 ||
            bounds.outHeight / sampleSize > targetSize * 2) {
            sampleSize *= 2
        }

        val options = BitmapFactory.Options().apply { inSampleSize = sampleSize }
        contentResolver.openInputStream(uri)?.use { input ->
            BitmapFactory.decodeStream(input, null, options)
        }
    }.getOrNull()

    private fun pasteClipboardImage(image: ClipboardImage) {
        val inputConnection = currentInputConnection ?: return
        val description = ClipDescription("Beam image", arrayOf(image.mimeType))
        val content = InputContentInfo(image.uri, description, null)
        val insertedAsContent = runCatching {
            inputConnection.commitContent(
                content,
                InputConnection.INPUT_CONTENT_GRANT_READ_URI_PERMISSION,
                null,
            )
        }.getOrDefault(false)
        if (insertedAsContent) {
            return
        }

        val pastedFromClipboard = runCatching {
            inputConnection.performContextMenuAction(android.R.id.paste)
        }.getOrDefault(false)
        if (pastedFromClipboard) {
            return
        }

        Toast.makeText(this, "Este campo não aceita imagens", Toast.LENGTH_SHORT).show()
    }

    private fun isImageMimeType(mimeType: String?): Boolean =
        mimeType?.startsWith("image/") == true

    private fun imageMimeType(uri: Uri, description: ClipDescription?): String? =
        description?.filterMimeTypes("image/*")?.firstOrNull()
            ?: contentResolver.getType(uri)?.takeIf(::isImageMimeType)

    // Layout de letras no padrão Samsung pt-BR: números como dica na fileira
    // superior, "ç" via toque longo no C e fileira do meio recuada em meia tecla.
    private fun letterRows(): List<Row> = listOf(
        Row(
            "qwertyuiop".mapIndexed { index, letter ->
                letterKey(letter, hint = "${(index + 1) % 10}")
            },
        ),
        Row("asdfghjkl".map { letterKey(it) }, sideSpacing = 0.5f),
        Row(
            listOf(shiftKey()) +
                "zxcvbnm".map { letter -> letterKey(letter, hint = if (letter == 'c') "ç" else null) } +
                backspaceKey(1.5f),
        ),
        Row(bottomRow(symbolsToggle = true)),
    )

    private fun symbols1Rows(): List<Row> = listOf(
        Row("1234567890".map { textKey(it) }),
        Row(
            listOf(
                textKey('+'),
                textKey('×'),
                textKey('÷'),
                textKey('='),
                textKey('/'),
                textKey('_', alternatives = "–—"),
                textKey('€'),
                textKey('£'),
                textKey('¥'),
                textKey('₩'),
            ),
        ),
        Row(
            listOf(
                Key("1/2", command = Command.Symbols2, weight = 1.5f, contentDescription = "Mostrar mais símbolos"),
                textKey('!'),
                textKey('@'),
                textKey('#'),
                textKey('$', alternatives = "¢₹₽"),
                textKey('%'),
                textKey('^'),
                textKey('&'),
                textKey('*'),
                textKey('('),
                textKey(')'),
                backspaceKey(1.5f),
            ),
        ),
        Row(bottomRow(symbolsToggle = false)),
    )

    private fun symbols2Rows(): List<Row> = listOf(
        Row("`~\\|{}[]<>".map { textKey(it) }),
        Row("°•○●□■♤♡◇♧".map { textKey(it) }),
        Row(
            listOf(
                Key("2/2", command = Command.Symbols1, weight = 1.5f, contentDescription = "Voltar aos símbolos"),
                textKey('☆', alternatives = "★"),
                textKey('▪'),
                textKey('¤'),
                textKey('《'),
                textKey('》'),
                textKey('¡'),
                textKey('¿'),
                backspaceKey(1.5f),
            ),
        ),
        Row(bottomRow(symbolsToggle = false)),
    )

    private fun bottomRow(symbolsToggle: Boolean): List<Key> = listOf(
        if (symbolsToggle) {
            Key("!#1", command = Command.Symbols1, weight = 1.4f, contentDescription = "Mostrar símbolos e números")
        } else {
            Key("ABC", command = Command.Letters, weight = 1.4f, contentDescription = "Mostrar letras")
        },
        Key(",", ",", function = true, alternatives = ";:!?¿¡"),
        Key("", " ", weight = 4.2f, contentDescription = "Espaço"),
        Key(".", ".", function = true, alternatives = ",?!;:…"),
        Key("↵", command = Command.Enter, weight = 1.4f, contentDescription = "Inserir nova linha"),
    )

    private fun shiftKey(): Key =
        Key("⇧", command = Command.Shift, weight = 1.5f, contentDescription = "Alternar maiúsculas")

    private fun backspaceKey(weight: Float): Key =
        Key("⌫", command = Command.Backspace, weight = weight, contentDescription = "Apagar")

    private fun textKey(value: Char, alternatives: String? = null): Key =
        Key(value.toString(), value.toString(), alternatives = alternatives)

    private fun letterKey(letter: Char, hint: String? = null): Key {
        val uppercase = shifted || capsLock
        val value = if (uppercase) letter.uppercaseChar() else letter
        val accents = AccentAlternatives[letter.lowercaseChar()]?.let { options ->
            if (uppercase) options.uppercase(Locale.ROOT) else options
        }
        val numberHint = hint?.takeIf { it.all(Char::isDigit) }.orEmpty()
        val alternatives = (numberHint + accents.orEmpty()).ifEmpty { null }
        return Key(value.toString(), value.toString(), alternatives = alternatives, hint = hint)
    }

    private val Int.dp: Int
        get() = (this * resources.displayMetrics.density).roundToInt()

    private val Float.dp: Float
        get() = this * resources.displayMetrics.density

    private data class Row(
        val keys: List<Key>,
        val sideSpacing: Float = 0f,
    )

    private data class Key(
        val label: String,
        val text: String? = null,
        val command: Command? = null,
        val weight: Float = 1f,
        val contentDescription: String = label,
        val alternatives: String? = null,
        val hint: String? = null,
        val function: Boolean = false,
    )

    private data class EnterAction(
        val label: String,
        val imeAction: Int,
    )

    private data class ClipboardImage(
        val uri: Uri,
        val mimeType: String,
    )

    private data class Palette(
        val surface: Int,
        val letterKey: Int,
        val functionKey: Int,
        val keyText: Int,
        val hintText: Int,
        val pressed: Int,
        val accent: Int,
        val onAccent: Int,
        val tray: Int,
        val trayText: Int,
        val previewFallback: Int,
        val raisedKeys: Boolean,
    )

    private enum class Command {
        Shift,
        Backspace,
        Symbols1,
        Symbols2,
        Letters,
        Enter,
    }

    private enum class KeyboardPage {
        Letters,
        Symbols1,
        Symbols2,
    }

    companion object {
        // Cores no estilo One UI (Samsung Keyboard) para os temas claro e escuro.
        private val LightPalette = Palette(
            surface = 0xFFDCE1E8.toInt(),
            letterKey = 0xFFFFFFFF.toInt(),
            functionKey = 0xFFC4CBD5.toInt(),
            keyText = 0xFF252525.toInt(),
            hintText = 0xFF7D848D.toInt(),
            pressed = 0x1F000000,
            accent = 0xFF0381FE.toInt(),
            onAccent = 0xFFFFFFFF.toInt(),
            tray = 0xFFEDF0F4.toInt(),
            trayText = 0xFF252525.toInt(),
            previewFallback = 0xFFC4CBD5.toInt(),
            raisedKeys = true,
        )
        private val DarkPalette = Palette(
            surface = 0xFF17191E.toInt(),
            letterKey = 0xFF343941.toInt(),
            functionKey = 0xFF24282E.toInt(),
            keyText = 0xFFF2F3F5.toInt(),
            hintText = 0xFF8E959D.toInt(),
            pressed = 0x33FFFFFF,
            accent = 0xFF3D8BFD.toInt(),
            onAccent = 0xFFFFFFFF.toInt(),
            tray = 0xFF24282E.toInt(),
            trayText = 0xFFF2F3F5.toInt(),
            previewFallback = 0xFF343941.toInt(),
            raisedKeys = false,
        )

        private const val KeyHeightDp = 50
        private const val KeyCornerRadiusDp = 12
        private const val BackspaceRepeatIntervalMs = 50L
        private const val DoubleTapShiftWindowMs = 300L

        private val AccentAlternatives = mapOf(
            'a' to "áàâãäåæª",
            'c' to "ç",
            'e' to "éèêë",
            'i' to "íìîï",
            'n' to "ñ",
            'o' to "óòôõöøœº",
            's' to "ß",
            'u' to "úùûü",
            'y' to "ýÿ",
        )

        fun isSelected(context: Context): Boolean {
            val selected = Settings.Secure.getString(
                context.contentResolver,
                Settings.Secure.DEFAULT_INPUT_METHOD,
            ) ?: return false
            val component = ComponentName(context, BeamInputMethodService::class.java)
            return selected == component.flattenToShortString() || selected == component.flattenToString()
        }
    }
}
