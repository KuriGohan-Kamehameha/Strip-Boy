package com.moonbench.thorlaunch

import android.app.ActivityManager
import android.app.ActivityOptions
import android.app.Presentation
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.ResolveInfo
import android.content.res.ColorStateList
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RadialGradient
import android.graphics.Shader
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.hardware.display.DisplayManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.Process
import android.provider.Settings
import android.util.Log
import android.view.Display
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TableLayout
import android.widget.TableRow
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import java.io.FileOutputStream

class MainActivity : AppCompatActivity() {
    private val handler = Handler(Looper.getMainLooper())
    private val activityManager by lazy { getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager }
    private val endpointFields = linkedMapOf<String, EditText>()
    private lateinit var infoTable: TableLayout
    private lateinit var actionPanel: LinearLayout
    private lateinit var manualSection: LinearLayout
    private lateinit var manualScroll: ScrollView
    private lateinit var refreshButton: Button
    private lateinit var launchButton: Button
    private lateinit var checkingView: TextView
    private lateinit var attributionView: TextView
    private lateinit var statusView: TextView
    private lateinit var gogCheckbox: CheckBox
    private lateinit var skipWizardCheckbox: CheckBox
    private lateinit var prefs: SharedPreferences
    private lateinit var screenRoot: FrameLayout
    private var bottomPresentation: BottomScreenPresentation? = null
    private var exitPollAttemptsRemaining = 0
    private var launcherClosing = false
    private var headlessAutoMode = false
    private var bifrostPluginInstalled = false
    private var bifrostPluginDetail = "query pending"
    private var bifrostPluginQueryInFlight = false
    private var bootSequenceGeneration = 0
    private var bootControlsReady = false
    private var pendingAutoLaunch = false
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        headlessAutoMode = prefs.getBoolean(KEY_SKIP_WIZARD, false)

        val screen = FrameLayout(this).apply {
            setBackgroundColor(COLOR_BLACK_GREEN)
            visibility = if (headlessAutoMode) View.INVISIBLE else View.VISIBLE
        }
        screenRoot = screen

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(30, 30, 30, 30)
            setBackgroundColor(COLOR_BLACK_GREEN)
        }

        val topPanel = panelContainer()
        val scroll = ScrollView(this)
        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Color.TRANSPARENT)
            setPadding(12, 12, 12, 12)
        }

        statusView = statusSummaryLine()

        infoTable = TableLayout(this).apply {
            setStretchAllColumns(false)
            setShrinkAllColumns(true)
        }

        val infoHeader = sectionLabel(getString(R.string.section_information)).apply {
            gravity = Gravity.CENTER_HORIZONTAL
        }

        content.addView(titlePlate())
        content.addView(infoHeader)
        content.addView(infoTable)

        scroll.addView(content)
        topPanel.addView(scroll, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            0,
            1f
        ))

        actionPanel = panelContainer().apply {
            setPadding(24, 24, 24, 24)
        }

        actionPanel.addView(sectionLabel(getString(R.string.section_actionables)).apply {
            gravity = Gravity.CENTER_HORIZONTAL
        })

        manualSection = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }

        manualScroll = ScrollView(this).apply {
            setBackgroundColor(Color.BLACK)
            isFillViewport = true
        }
        manualScroll.addView(manualSection)

        addEndpointField(manualSection, getString(R.string.field_gamenative_package), DEFAULT_GAMENATIVE_PACKAGE)
        addEndpointField(manualSection, getString(R.string.field_gamenative_shortcut), DEFAULT_GAMENATIVE_ACTIVITY)
        addEndpointField(manualSection, getString(R.string.field_companion_package), DEFAULT_COMPANION_PACKAGE)
        addEndpointField(manualSection, getString(R.string.field_bifrost_package), DEFAULT_BIFROST_PACKAGE)

        gogCheckbox = CheckBox(this).apply {
            text = getString(R.string.gog_checkbox)
            styleCheckBox(this)
        }

        skipWizardCheckbox = CheckBox(this).apply {
            text = getString(R.string.launch_automatically_next_time_onwards)
            styleCheckBox(this)
        }

        refreshButton = Button(this).apply {
            text = getString(R.string.refresh_button)
            styleButton(this, compact = true)
            setOnClickListener { refreshWizardState() }
        }

        checkingView = TextView(this).apply {
            text = getString(R.string.status_checking_short)
            gravity = Gravity.CENTER
            styleTerminalText(this, sizeSp = 28f, bold = true)
            setPadding(0, 42, 0, 42)
        }

        launchButton = Button(this).apply {
            text = getString(R.string.launch_button)
            setOnClickListener { launchBoth() }
            styleButton(this, compact = false)
            gravity = Gravity.CENTER
        }

        attributionView = attributionLine()

        actionPanel.addView(manualScroll, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            0,
            1f
        ))
        actionPanel.addView(refreshButton)
        actionPanel.addView(checkingView)
        actionPanel.addView(launchButton)
        actionPanel.addView(skipWizardCheckbox)
        actionPanel.addView(attributionView)

        root.addView(topPanel, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            0,
            0.48f
        ))
        root.addView(actionPanel, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            0,
            0.52f
        ))
        screen.addView(root, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT
        ))
        screen.addView(CrtOverlayView(this), FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT
        ))
        setContentView(screen)

        loadSavedState()
        refreshWizardState()
        if (!headlessAutoMode) {
            showBottomScreenIfAvailable()
        }

        if (savedInstanceState == null) {
            handler.post {
                pendingAutoLaunch = intent.getBooleanExtra(EXTRA_AUTO_LAUNCH, false) || skipWizardCheckbox.isChecked
                maybeRunPendingAutoLaunch()
            }
        }
    }

    private fun launchBoth() {
        persistUserInputs()
        val launchState = collectState()
        if (!launchState.canLaunch) {
            statusView.text = launchState.blockingMessage
            refreshWizardState()
            return
        }
        statusView.text = getString(R.string.status_launching)
        if (!skipWizardCheckbox.isChecked) {
            setManualLaunchStickGreen()
        }
        val launched = launchGameNative(launchState, Display.DEFAULT_DISPLAY)
        if (launched) {
            handler.postDelayed({
                val bottomDisplayId = findBottomDisplayId()
                val companionLaunched = launchPackage(launchState.companionPackage, bottomDisplayId)
                if (companionLaunched) {
                    statusView.text = getString(R.string.status_launched)
                    scheduleGracefulExit(launchState)
                    handler.postDelayed({
                        refocusCompanionOnBottomDisplay(launchState)
                    }, COMPANION_REFOCUS_DELAY_MS)
                }
            }, SECOND_LAUNCH_DELAY_MS)
        }
    }

    private fun refocusCompanionOnBottomDisplay(state: WizardState) {
        val bottomDisplayId = findBottomDisplayId() ?: return
        val options = ActivityOptions.makeBasic().apply { setLaunchDisplayId(bottomDisplayId) }
        val intent = Intent(Intent.ACTION_MAIN).apply {
            setClassName(state.companionPackage, "com.unity3d.player.UnityPlayerNativeActivity")
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT)
        }
        runCatching { startActivity(intent, options.toBundle()) }
            .onFailure { Log.w(TAG, "companion bottom refocus failed", it) }
    }

    private fun maybeAutoLaunch() {
        pendingAutoLaunch = skipWizardCheckbox.isChecked
        maybeRunPendingAutoLaunch()
    }

    private fun maybeRunPendingAutoLaunch() {
        if (!pendingAutoLaunch || bifrostPluginQueryInFlight) {
            return
        }
        val state = collectState()
        if (state.canLaunch) {
            pendingAutoLaunch = false
            launchBoth()
        } else if (headlessAutoMode) {
            pendingAutoLaunch = false
            revealWizardForFailure(state)
        }
    }

    private fun revealWizardForFailure(state: WizardState) {
        headlessAutoMode = false
        screenRoot.visibility = View.VISIBLE
        statusView.text = state.blockingMessage
        renderInfoTable(state, animate = true)
        updateActionPanel(state)
        showBottomScreenIfAvailable()
        bottomPresentation?.updateState(state)
    }

    private fun launchPackage(packageName: String, displayId: Int? = null): Boolean {
        val launchIntent = packageManager.getLaunchIntentForPackage(packageName)
            ?: Intent(Intent.ACTION_MAIN).apply {
                addCategory(Intent.CATEGORY_LAUNCHER)
                setPackage(packageName)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }

        if (displayId != null) {
            val options = ActivityOptions.makeBasic().apply { setLaunchDisplayId(displayId) }
            return runCatching { startActivity(launchIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), options.toBundle()) }
                .map { true }
                .onFailure {
                    statusView.text = getString(R.string.status_missing_package, packageName)
                }
                .getOrDefault(false)
        }

        return runCatching { startActivity(launchIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
            .map { true }
            .onFailure {
                statusView.text = getString(R.string.status_missing_package, packageName)
            }
            .getOrDefault(false)
    }

    private fun launchGameNative(state: WizardState, displayId: Int? = null): Boolean {
        val launchIntent = Intent("app.gamenative.LAUNCH_GAME").apply {
            setClassName(state.gameNativePackage, "app.gamenative.MainActivity")
            val appIdInt = state.gameNativeActivityName.toIntOrNull() ?: DEFAULT_GOG_APP_ID.toInt()
            putExtra("app_id", appIdInt)
            putExtra("game_source", DEFAULT_GAME_SOURCE)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        }

        if (displayId != null) {
            val options = ActivityOptions.makeBasic().apply { setLaunchDisplayId(displayId) }
            return runCatching { startActivity(launchIntent, options.toBundle()) }
                .map { true }
                .onFailure { statusView.text = getString(R.string.status_missing_gamenative_shortcut) }
                .getOrDefault(false)
        }

        return runCatching { startActivity(launchIntent) }
            .map { true }
            .onFailure { statusView.text = getString(R.string.status_missing_gamenative_shortcut) }
            .getOrDefault(false)
    }

    private fun setManualLaunchStickGreen() {
        Thread({ writeManualLaunchStickGreen() }, "thorlaunch-led-prime").start()
        val intent = Intent(BIFROST_ACTION_DISPLAY).apply {
            setClassName(DEFAULT_BIFROST_PACKAGE, BIFROST_RECEIVER_CLASS)
            putExtra(BIFROST_EXTRA_API_VERSION, BIFROST_API_VERSION)
            putExtra(BIFROST_EXTRA_EFFECT, BIFROST_EFFECT_STATIC)
            putExtra(BIFROST_EXTRA_COLOR, COLOR_MANUAL_LAUNCH_GREEN)
            putExtra(BIFROST_EXTRA_COLOR_RIGHT, COLOR_MANUAL_LAUNCH_GREEN)
            putExtra(BIFROST_EXTRA_INTENSITY, MANUAL_LAUNCH_INTENSITY)
            putExtra(BIFROST_EXTRA_PRIORITY, MANUAL_LAUNCH_PRIORITY)
            putExtra(BIFROST_EXTRA_DURATION_MS, MANUAL_LAUNCH_DURATION_MS)
        }
        runCatching {
            sendOrderedBroadcast(
                intent,
                BIFROST_PERMISSION,
                object : BroadcastReceiver() {
                    override fun onReceive(context: Context, receivedIntent: Intent) {
                        Log.i(TAG, "manual launch stick green result=$resultCode")
                    }
                },
                null,
                BIFROST_RESULT_NOT_FOUND,
                null,
                null
            )
        }.onFailure { Log.w(TAG, "manual launch stick green send failed", it) }
    }

    private fun writeManualLaunchStickGreen() {
        MANUAL_LAUNCH_LED_PATHS.forEach { path ->
            runCatching {
                FileOutputStream(path).use { stream ->
                    stream.write(MANUAL_LAUNCH_LED_PAYLOAD.toByteArray())
                }
            }.onFailure { Log.w(TAG, "manual launch sysfs green failed: $path", it) }
        }
    }

    private fun scheduleGracefulExit(state: WizardState) {
        exitPollAttemptsRemaining = EXIT_POLL_ATTEMPTS
        handler.postDelayed({ closeLauncher() }, POST_LAUNCH_CLOSE_DELAY_MS)
        handler.postDelayed({ waitForForegroundAndExit(state) }, EXIT_POLL_INTERVAL_MS)
    }

    private fun waitForForegroundAndExit(state: WizardState) {
        if (areLaunchTargetsForeground(state)) {
            statusView.text = getString(R.string.status_self_terminating)
            closeLauncher()
            return
        }

        if (exitPollAttemptsRemaining <= 0) {
            closeLauncher()
            return
        }

        exitPollAttemptsRemaining--
        statusView.text = getString(R.string.status_waiting_for_foreground)
        handler.postDelayed({ waitForForegroundAndExit(state) }, EXIT_POLL_INTERVAL_MS)
    }

    private fun closeLauncher() {
        if (launcherClosing) {
            return
        }
        launcherClosing = true
        bottomPresentation?.let { presentation ->
            runCatching { presentation.dismiss() }
        }
        bottomPresentation = null
        finishAndRemoveTask()
        finishAffinity()
        handler.postDelayed({ Process.killProcess(Process.myPid()) }, SELF_KILL_DELAY_MS)
    }

    private fun areLaunchTargetsForeground(state: WizardState): Boolean {
        return isPackageForeground(state.gameNativePackage) && isPackageForeground(state.companionPackage)
    }

    private fun isPackageForeground(packageName: String): Boolean {
        val processes = activityManager.runningAppProcesses ?: return false
        return processes.any { processInfo ->
            processInfo.pkgList.contains(packageName) &&
                processInfo.importance <= ActivityManager.RunningAppProcessInfo.IMPORTANCE_VISIBLE
        }
    }

    private fun refreshWizardState() {
        persistUserInputs()
        val state = collectState()
        statusView.text = state.summaryLine
        renderInfoTable(state, animate = true)
        updateActionPanel(state)
        bottomPresentation?.updateState(state)
        queryBifrostPlugin(state.bifrostInstalled)
        if (endpointFields[KEY_GAMENATIVE_ACTIVITY]?.text?.isNullOrBlank() == true && state.gameNativeActivityName.isNotBlank()) {
            endpointFields[KEY_GAMENATIVE_ACTIVITY]?.setText(state.gameNativeActivityName)
        }
    }

    private fun collectState(): WizardState {
        val gameNativePackage = endpointFields[KEY_GAMENATIVE]?.text?.toString().orEmpty()
            .ifBlank { DEFAULT_GAMENATIVE_PACKAGE }
        val gameNativeActivity = endpointFields[KEY_GAMENATIVE_ACTIVITY]?.text?.toString().orEmpty()
            .takeIf { it.toIntOrNull() != null }
            .orEmpty()
        val companionPackage = endpointFields[KEY_COMPANION]?.text?.toString().orEmpty()
            .ifBlank { DEFAULT_COMPANION_PACKAGE }
        val bifrostPackage = endpointFields[KEY_BIFROST]?.text?.toString().orEmpty()
            .ifBlank { DEFAULT_BIFROST_PACKAGE }

        val gameNativeInstalled = isPackageInstalled(gameNativePackage)
        val companionInstalled = isPatchedCompanionInstalled(companionPackage)
        val bifrostInstalled = isPackageInstalled(bifrostPackage)
        val pipBoyPluginInstalled = bifrostInstalled && bifrostPluginInstalled
        val bottomDisplayAvailable = findBottomDisplayId() != null
        val resolvedGameNativeActivity = gameNativeActivity.ifBlank { DEFAULT_GOG_APP_ID }
        val shortcutReady = resolvedGameNativeActivity.isNotBlank()

        val fullyReady = gameNativeInstalled && companionInstalled && shortcutReady && bottomDisplayAvailable && bifrostInstalled && pipBoyPluginInstalled
        val blockingMessage = when {
            !companionInstalled -> getString(R.string.block_companion_missing, companionPackage)
            !gameNativeInstalled -> getString(R.string.block_gamenative_missing, gameNativePackage)
            !shortcutReady -> getString(R.string.block_shortcut_missing, gameNativePackage)
            !bottomDisplayAvailable -> getString(R.string.block_bottom_display_missing)
            !bifrostInstalled -> getString(R.string.block_bifrost_missing, bifrostPackage)
            !pipBoyPluginInstalled -> getString(R.string.block_bifrost_plugin_missing)
            else -> getString(R.string.status_ready)
        }

        return WizardState(
            gameNativePackage = gameNativePackage,
            gameNativeActivityName = resolvedGameNativeActivity,
            companionPackage = companionPackage,
            bifrostPackage = bifrostPackage,
            canLaunch = fullyReady,
            fullyReady = fullyReady,
            blockingMessage = blockingMessage,
            canAutoLaunch = fullyReady && skipWizardCheckbox.isChecked,
            summaryLine = when {
                fullyReady -> getString(R.string.status_ready)
                else -> blockingMessage
            },
            gameNativeInstalled = gameNativeInstalled,
            shortcutFound = shortcutReady,
            shortcutLabel = resolvedGameNativeActivity,
            companionInstalled = companionInstalled,
            bottomDisplayAvailable = bottomDisplayAvailable,
            bifrostInstalled = bifrostInstalled,
            bifrostPluginInstalled = pipBoyPluginInstalled,
            bifrostPluginDetail = if (bifrostInstalled) bifrostPluginDetail else getString(R.string.bifrost_plugin_needs_app),
            gogConfirmed = true,
            batteryIgnored = true
        )
    }

    private fun persistUserInputs() {
        prefs.edit()
            .putString(KEY_GAMENATIVE, endpointFields[KEY_GAMENATIVE]?.text?.toString())
            .putString(KEY_GAMENATIVE_ACTIVITY, endpointFields[KEY_GAMENATIVE_ACTIVITY]?.text?.toString())
            .putString(KEY_COMPANION, endpointFields[KEY_COMPANION]?.text?.toString())
            .putString(KEY_BIFROST, endpointFields[KEY_BIFROST]?.text?.toString())
            .putBoolean(KEY_GOG_CONFIRMED, gogCheckbox.isChecked)
            .putBoolean(KEY_SKIP_WIZARD, skipWizardCheckbox.isChecked)
            .apply()
    }

    private fun loadSavedState() {
        endpointFields[KEY_GAMENATIVE]?.setText(
            prefs.getString(KEY_GAMENATIVE, DEFAULT_GAMENATIVE_PACKAGE)
        )
        endpointFields[KEY_GAMENATIVE_ACTIVITY]?.setText(
            prefs.getString(KEY_GAMENATIVE_ACTIVITY, "")
        )
        endpointFields[KEY_COMPANION]?.setText(
            prefs.getString(KEY_COMPANION, DEFAULT_COMPANION_PACKAGE)
        )
        endpointFields[KEY_BIFROST]?.setText(
            prefs.getString(KEY_BIFROST, DEFAULT_BIFROST_PACKAGE)
        )
        gogCheckbox.isChecked = true
        skipWizardCheckbox.isChecked = prefs.getBoolean(KEY_SKIP_WIZARD, false)
    }

    private fun addEndpointField(container: LinearLayout, label: String, fallback: String) {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, 0, 0, 20)
        }
        val fieldLabel = TextView(this).apply {
            text = label
            styleTerminalText(this, sizeSp = 14f, bold = true)
        }
        val edit = EditText(this).apply {
            hint = fallback
            setSingleLine(true)
            setTextColor(COLOR_GREEN)
            setHintTextColor(COLOR_DIM_GREEN)
            background = fieldBackground()
            typeface = Typeface.create("monospace", Typeface.NORMAL)
            setShadowLayer(3f, 0f, 0f, COLOR_GLOW)
            setPadding(14, 8, 14, 8)
        }
        when (fallback) {
            DEFAULT_GAMENATIVE_PACKAGE -> endpointFields[KEY_GAMENATIVE] = edit
            DEFAULT_GAMENATIVE_ACTIVITY -> endpointFields[KEY_GAMENATIVE_ACTIVITY] = edit
            DEFAULT_COMPANION_PACKAGE -> endpointFields[KEY_COMPANION] = edit
            DEFAULT_BIFROST_PACKAGE -> endpointFields[KEY_BIFROST] = edit
        }
        row.addView(fieldLabel)
        row.addView(edit)
        container.addView(row)
    }

    private fun sectionLabel(text: String): TextView = TextView(this).apply {
        this.text = text
        styleTerminalText(this, sizeSp = 18f, bold = true)
        setPadding(0, 10, 0, 6)
    }

    private fun titlePlate(): TextView = TextView(this).apply {
        text = getString(R.string.pipboy_header)
        gravity = Gravity.CENTER
        styleTerminalText(this, sizeSp = 24f, bold = true)
        background = GradientDrawable().apply {
            setColor(COLOR_PANEL)
            cornerRadius = 4f
            setStroke(2, COLOR_GREEN)
        }
        setPadding(10, 14, 10, 14)
    }

    private fun attributionLine(): TextView = TextView(this).apply {
        text = getString(R.string.attribution_line)
        gravity = Gravity.CENTER
        styleTerminalText(this, sizeSp = 11f, bold = false, color = COLOR_DIM_GREEN)
        setPadding(8, 14, 8, 0)
    }

    private fun renderInfoTable(state: WizardState, animate: Boolean) {
        bootSequenceGeneration++
        val generation = bootSequenceGeneration
        infoTable.removeAllViews()
        if (animate) {
            setBootControlsVisible(false)
        }
        val rows = listOf(
            StatusRow(getString(R.string.check_gamenative_installed), state.gameNativeInstalled, state.gameNativePackage),
            StatusRow(getString(R.string.check_gamenative_shortcut_found), state.shortcutFound, state.shortcutLabel.ifBlank { getString(R.string.shortcut_manual_instructions) }),
            StatusRow(getString(R.string.check_companion_installed), state.companionInstalled, state.companionPackage),
            StatusRow(getString(R.string.check_bottom_display_available), state.bottomDisplayAvailable, getString(R.string.bottom_display_detail)),
            StatusRow(getString(R.string.check_bifrost_app_installed), state.bifrostInstalled, state.bifrostPackage),
            StatusRow(getString(R.string.check_bifrost_plugin_installed), state.bifrostPluginInstalled, state.bifrostPluginDetail)
        )

        var rowDelayMs = 0L
        rows.forEach { row ->
            if (animate) {
                val delayMs = rowDelayMs
                handler.postDelayed({
                    if (generation == bootSequenceGeneration) {
                        addStatusRow(infoTable, row.label, row.ok, row.detail, animate = true, generation = generation)
                    }
                }, delayMs)
                rowDelayMs += bootRowDuration(row)
            } else {
                addStatusRow(infoTable, row.label, row.ok, row.detail)
            }
        }
        if (animate) {
            handler.postDelayed({
                if (generation == bootSequenceGeneration) {
                    setBootControlsVisible(true)
                    val finalState = collectState()
                    statusView.text = finalState.summaryLine
                    updateActionPanel(finalState)
                    bottomPresentation?.updateState(finalState)
                }
            }, rowDelayMs)
        } else {
            setBootControlsVisible(true)
        }
    }

    private fun setBootControlsVisible(ready: Boolean) {
        bootControlsReady = ready
        checkingView.visibility = if (ready) View.GONE else View.VISIBLE
        launchButton.visibility = if (ready) View.VISIBLE else View.GONE
        skipWizardCheckbox.visibility = if (ready) View.VISIBLE else View.GONE
        attributionView.visibility = if (ready) View.VISIBLE else View.GONE
        bottomPresentation?.updateBootControls(ready)
    }

    private fun bootRowDuration(row: StatusRow): Long {
        return ((row.label.length + row.detail.length) * BOOT_CHAR_DELAY_MS) + BOOT_CHECK_DELAY_MS + BOOT_ROW_GAP_MS
    }

    private fun addStatusRow(table: TableLayout, label: String, ok: Boolean, detail: String, animate: Boolean = false, generation: Int = bootSequenceGeneration) {
        val row = TableRow(this).apply {
            setPadding(0, 12, 0, 12)
        }
        val icon = TextView(this).apply {
            text = if (animate) "" else if (ok) "✓" else "!"
            gravity = Gravity.CENTER
            styleTerminalText(this, sizeSp = 22f, bold = true, color = if (ok) COLOR_GREEN else COLOR_DIM_GREEN)
            setPadding(0, 0, 20, 0)
        }
        val labelView = TextView(this).apply {
            text = if (animate) "" else label
            styleTerminalText(this, sizeSp = 16f, bold = false)
            setPadding(0, 0, 12, 0)
        }
        val detailView = TextView(this).apply {
            text = if (animate) "" else detail
            styleTerminalText(this, sizeSp = 13.5f, bold = false, color = COLOR_DIM_GREEN)
            gravity = Gravity.END
            setSingleLine(true)
            includeFontPadding = false
            setPadding(0, 0, 28, 0)
        }
        row.addView(icon, TableRow.LayoutParams(0, TableRow.LayoutParams.WRAP_CONTENT, 0.10f))
        row.addView(labelView, TableRow.LayoutParams(0, TableRow.LayoutParams.WRAP_CONTENT, 0.42f))
        row.addView(detailView, TableRow.LayoutParams(0, TableRow.LayoutParams.WRAP_CONTENT, 0.48f))
        table.addView(row)
        if (animate) {
            animateStatusRow(label, detail, ok, icon, labelView, detailView, generation)
        }
    }

    private fun animateStatusRow(
        label: String,
        detail: String,
        ok: Boolean,
        icon: TextView,
        labelView: TextView,
        detailView: TextView,
        generation: Int
    ) {
        var delayMs = 0L
        for (charCount in 1..label.length) {
            val visibleCount = charCount
            handler.postDelayed({
                if (generation == bootSequenceGeneration) {
                    labelView.text = label.take(visibleCount)
                }
            }, delayMs)
            delayMs += BOOT_CHAR_DELAY_MS
        }
        for (charCount in 1..detail.length) {
            val visibleCount = charCount
            handler.postDelayed({
                if (generation == bootSequenceGeneration) {
                    detailView.text = detail.take(visibleCount)
                }
            }, delayMs)
            delayMs += BOOT_CHAR_DELAY_MS
        }
        handler.postDelayed({
            if (generation == bootSequenceGeneration) {
                icon.text = if (ok) "✓" else "!"
            }
        }, delayMs + BOOT_CHECK_DELAY_MS)
    }

    private fun statusSummaryLine(): TextView = TextView(this).apply {
        text = getString(R.string.status_collecting)
        styleTerminalText(this, sizeSp = 16f, bold = false)
        gravity = Gravity.CENTER_HORIZONTAL
        setPadding(0, 18, 0, 20)
    }

    private fun updateActionPanel(state: WizardState) {
        if (bottomPresentation != null) {
            actionPanel.visibility = View.GONE
            return
        }
        val showSetupControls = !state.fullyReady
        manualSection.visibility = if (showSetupControls) android.view.View.VISIBLE else android.view.View.GONE
        manualScroll.visibility = if (showSetupControls) android.view.View.VISIBLE else android.view.View.GONE
        refreshButton.visibility = if (showSetupControls) android.view.View.VISIBLE else android.view.View.GONE
        actionPanel.gravity = Gravity.CENTER_VERTICAL
        launchButton.layoutParams = if (showSetupControls) {
            LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                dp(180)
            ).apply {
                topMargin = 18
            }
        } else {
            LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                dp(260)
            ).apply {
                topMargin = 18
                bottomMargin = 18
            }
        }
        launchButton.textSize = if (showSetupControls) 24f else 32f
        launchButton.minimumHeight = if (showSetupControls) 180 else 260
        launchButton.setPadding(0, if (showSetupControls) 36 else 56, 0, if (showSetupControls) 36 else 56)
        launchButton.isEnabled = state.canLaunch
        launchButton.alpha = if (state.canLaunch) 1f else 0.5f
        if (checkingView.visibility != View.VISIBLE) {
            skipWizardCheckbox.visibility = View.VISIBLE
            attributionView.visibility = View.VISIBLE
        }
    }

    private fun panelContainer(): LinearLayout = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(18, 18, 18, 18)
        background = GradientDrawable().apply {
            setColor(COLOR_PANEL)
            cornerRadius = 6f
            setStroke(3, COLOR_DIM_GREEN)
        }
    }

    private fun styledButtonBackground(color: Int): GradientDrawable = GradientDrawable().apply {
        setColor(COLOR_PANEL)
        cornerRadius = 4f
        setStroke(3, color)
    }

    private fun fieldBackground(): GradientDrawable = GradientDrawable().apply {
        setColor(COLOR_FIELD)
        cornerRadius = 3f
        setStroke(1, COLOR_DIM_GREEN)
    }

    private fun styleButton(button: Button, compact: Boolean) {
        button.textSize = if (compact) 14f else 26f
        button.typeface = Typeface.create("monospace", Typeface.BOLD)
        button.setTextColor(COLOR_GREEN)
        button.setShadowLayer(5f, 0f, 0f, COLOR_GLOW)
        button.background = styledButtonBackground(COLOR_GREEN)
        button.includeFontPadding = false
        button.setPadding(12, if (compact) 12 else 24, 12, if (compact) 12 else 24)
    }

    private fun styleCheckBox(checkBox: CheckBox) {
        styleTerminalText(checkBox, sizeSp = 14f, bold = false)
        checkBox.buttonTintList = ColorStateList.valueOf(COLOR_GREEN)
    }

    private fun styleTerminalText(textView: TextView, sizeSp: Float, bold: Boolean, color: Int = COLOR_GREEN) {
        textView.textSize = sizeSp
        textView.setTextColor(color)
        textView.typeface = Typeface.create("monospace", if (bold) Typeface.BOLD else Typeface.NORMAL)
        textView.setShadowLayer(4f, 0f, 0f, COLOR_GLOW)
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    private fun findBottomDisplayId(): Int? {
        val displayManager = getSystemService(Context.DISPLAY_SERVICE) as? DisplayManager ?: return null
        return displayManager.displays.firstOrNull { candidate ->
            candidate.displayId != Display.DEFAULT_DISPLAY &&
                candidate.flags and Display.FLAG_PRESENTATION != 0
        }?.displayId
    }

    private fun relaunchOnDisplay(displayId: Int) {
        val launchIntent = packageManager.getLaunchIntentForPackage(packageName) ?: return
        val options = ActivityOptions.makeBasic().apply {
            setLaunchDisplayId(displayId)
        }
        launchIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
        startActivity(launchIntent, options.toBundle())
    }

    private fun showBottomScreenIfAvailable() {
        val displayId = findBottomDisplayId() ?: return
        if (display?.displayId == displayId) {
            return
        }
        val displayManager = getSystemService(Context.DISPLAY_SERVICE) as? DisplayManager ?: return
        val targetDisplay = displayManager.getDisplay(displayId) ?: return
        
        val presentation = BottomScreenPresentation(this, targetDisplay)
        runCatching { presentation.show() }
            .onSuccess {
                bottomPresentation = presentation
                actionPanel.visibility = View.GONE
            }
            .onFailure {
                bottomPresentation = null
                it.printStackTrace()
                actionPanel.visibility = View.VISIBLE
            }
    }

    private fun isPackageInstalled(packageName: String): Boolean = runCatching {
        packageManager.getPackageInfo(packageName, 0)
        true
    }.getOrDefault(false)

    private fun isPatchedCompanionInstalled(packageName: String): Boolean = runCatching {
        packageManager.getActivityInfo(
            ComponentName(packageName, COMPANION_LAUNCHER_ACTIVITY),
            0
        )
        true
    }.getOrDefault(false)

    private fun queryBifrostPlugin(bifrostInstalled: Boolean) {
        if (!bifrostInstalled) {
            bifrostPluginInstalled = false
            bifrostPluginDetail = getString(R.string.bifrost_plugin_needs_app)
            return
        }
        if (bifrostPluginQueryInFlight) {
            return
        }

        bifrostPluginQueryInFlight = true
        val queryIntent = Intent(BIFROST_ACTION_QUERY_PLUGIN).apply {
            setClassName(DEFAULT_BIFROST_PACKAGE, BIFROST_RECEIVER_CLASS)
            putExtra(BIFROST_EXTRA_API_VERSION, BIFROST_API_VERSION)
            putExtra(BIFROST_EXTRA_PLUGIN_ID, BIFROST_PIPBOY_PLUGIN_ID)
        }

        sendOrderedBroadcast(
            queryIntent,
            BIFROST_PERMISSION,
            object : BroadcastReceiver() {
                override fun onReceive(context: Context, intent: Intent) {
                    bifrostPluginQueryInFlight = false
                    bifrostPluginInstalled = resultCode == BIFROST_RESULT_ACCEPTED
                    bifrostPluginDetail = if (bifrostPluginInstalled) {
                        resultData ?: BIFROST_PIPBOY_PLUGIN_ID
                    } else {
                        getString(R.string.bifrost_plugin_missing_detail)
                    }
                    val updatedState = collectState()
                    renderInfoTable(updatedState, animate = true)
                    updateActionPanel(updatedState)
                    bottomPresentation?.updateState(updatedState)
                    maybeRunPendingAutoLaunch()
                }
            },
            null,
            BIFROST_RESULT_NOT_FOUND,
            null,
            null
        )
    }

    private companion object {
        const val PREFS_NAME = "thor_launch_wrapper_prefs"
        const val TAG = "ThorLaunch"
        const val KEY_GAMENATIVE = "gamenative"
        const val KEY_GAMENATIVE_ACTIVITY = "gamenative_activity"
        const val KEY_COMPANION = "companion"
        const val KEY_BIFROST = "bifrost"
        const val KEY_GOG_CONFIRMED = "gog_confirmed"
        const val KEY_SKIP_WIZARD = "skip_wizard"
        const val DEFAULT_GAMENATIVE_PACKAGE = "app.gamenative"
        const val DEFAULT_GAMENATIVE_ACTIVITY = ""
        const val DEFAULT_GOG_APP_ID = "1998527297"
        const val DEFAULT_GAME_SOURCE = "GOG"
        const val DEFAULT_COMPANION_PACKAGE = "com.bethsoft.falloutcompanionapp"
        const val COMPANION_LAUNCHER_ACTIVITY = "io.pipboy.thor.LauncherActivity"
        const val DEFAULT_BIFROST_PACKAGE = "com.moonbench.bifrost"
        const val BIFROST_RECEIVER_CLASS = "com.moonbench.bifrost.external.ExternalApiReceiver"
        const val BIFROST_PERMISSION = "com.moonbench.bifrost.permission.CONTROL_LEDS"
        const val BIFROST_ACTION_DISPLAY = "com.moonbench.bifrost.api.ACTION_DISPLAY"
        const val BIFROST_ACTION_QUERY_PLUGIN = "com.moonbench.bifrost.api.ACTION_QUERY_PLUGIN"
        const val BIFROST_EXTRA_API_VERSION = "apiVersion"
        const val BIFROST_EXTRA_PLUGIN_ID = "pluginId"
        const val BIFROST_EXTRA_EFFECT = "effect"
        const val BIFROST_EXTRA_COLOR = "color"
        const val BIFROST_EXTRA_COLOR_RIGHT = "colorRight"
        const val BIFROST_EXTRA_INTENSITY = "intensity"
        const val BIFROST_EXTRA_PRIORITY = "priority"
        const val BIFROST_EXTRA_DURATION_MS = "durationMs"
        const val BIFROST_API_VERSION = 1
        const val BIFROST_EFFECT_STATIC = "STATIC"
        const val BIFROST_RESULT_ACCEPTED = 0
        const val BIFROST_RESULT_NOT_FOUND = 1
        const val BIFROST_PIPBOY_PLUGIN_ID = "fallout4-pipboy"
        const val MANUAL_LAUNCH_INTENSITY = 77
        const val MANUAL_LAUNCH_PRIORITY = 45
        const val MANUAL_LAUNCH_DURATION_MS = 12_000L
        const val COLOR_MANUAL_LAUNCH_GREEN = 0xFF00FF66.toInt()
        const val MANUAL_LAUNCH_LED_PAYLOAD = "1-0:77:0:255\n"
        val MANUAL_LAUNCH_LED_PATHS = arrayOf(
            "/sys/class/sn3112l/led/brightness",
            "/sys/class/sn3112r/led/brightness"
        )
        const val EXTRA_AUTO_LAUNCH = "com.moonbench.thorlaunch.AUTO_LAUNCH"
        const val SECOND_LAUNCH_DELAY_MS = 650L
        const val COMPANION_REFOCUS_DELAY_MS = 1600L
        const val EXIT_POLL_INTERVAL_MS = 250L
        const val EXIT_POLL_ATTEMPTS = 40
        const val POST_LAUNCH_CLOSE_DELAY_MS = 1800L
        const val SELF_KILL_DELAY_MS = 250L
        const val BOOT_CHAR_DELAY_MS = 38L
        const val BOOT_CHECK_DELAY_MS = 140L
        const val BOOT_ROW_GAP_MS = 260L
        const val CRT_FRAME_MS = 42L
        const val CRT_FRAME_LOOP = 240
        const val MAX_SCANLINES = 640
        const val MAX_PHOSPHOR_STRIPES = 480
        const val MAX_NOISE_SPECKS = 70
        const val COLOR_GREEN = 0xFF00FF66.toInt()
        const val COLOR_DIM_GREEN = 0xFF008844.toInt()
        const val COLOR_GLOW = 0xAA00FF66.toInt()
        const val COLOR_BLACK_GREEN = 0xFF010701.toInt()
        const val COLOR_PANEL = 0xD0061607.toInt()
        const val COLOR_FIELD = 0xCC020C03.toInt()
    }

    private data class WizardState(
        val gameNativePackage: String,
        val gameNativeActivityName: String,
        val companionPackage: String,
        val bifrostPackage: String,
        val canLaunch: Boolean,
        val fullyReady: Boolean,
        val canAutoLaunch: Boolean,
        val blockingMessage: String,
        val summaryLine: String,
        val gameNativeInstalled: Boolean,
        val shortcutFound: Boolean,
        val shortcutLabel: String,
        val companionInstalled: Boolean,
        val bottomDisplayAvailable: Boolean,
        val bifrostInstalled: Boolean,
        val bifrostPluginInstalled: Boolean,
        val bifrostPluginDetail: String,
        val gogConfirmed: Boolean,
        val batteryIgnored: Boolean
    )

    private data class StatusRow(
        val label: String,
        val ok: Boolean,
        val detail: String
    )

    private inner class BottomScreenPresentation(
        context: Context,
        display: Display
    ) : Presentation(context, display) {
        private lateinit var bottomStatus: TextView
        private lateinit var bottomCheckingView: TextView
        private lateinit var bottomLaunchButton: Button
        private lateinit var bottomSkipCheckbox: CheckBox
        private lateinit var bottomAttributionView: TextView

        override fun onCreate(savedInstanceState: Bundle?) {
            super.onCreate(savedInstanceState)
            val root = LinearLayout(context).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(36, 36, 36, 36)
                setBackgroundColor(COLOR_BLACK_GREEN)
            }

            bottomStatus = TextView(context).apply {
                gravity = Gravity.CENTER_HORIZONTAL
                styleTerminalText(this, sizeSp = 18f, bold = false)
            }

            bottomCheckingView = TextView(context).apply {
                text = getString(R.string.status_checking_short)
                gravity = Gravity.CENTER
                styleTerminalText(this, sizeSp = 32f, bold = true)
            }

            bottomLaunchButton = Button(context).apply {
                text = getString(R.string.launch_button)
                styleButton(this, compact = false)
                gravity = Gravity.CENTER
                setOnClickListener { launchBoth() }
            }

            bottomSkipCheckbox = CheckBox(context).apply {
                text = getString(R.string.launch_automatically_next_time_onwards)
                styleCheckBox(this)
                isChecked = skipWizardCheckbox.isChecked
                setOnCheckedChangeListener { _, isChecked ->
                    skipWizardCheckbox.isChecked = isChecked
                    persistUserInputs()
                }
            }

            bottomAttributionView = attributionLine()

            root.addView(bottomStatus)
            root.addView(bottomCheckingView, LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                0,
                1f
            ).apply {
                topMargin = 24
                bottomMargin = 24
            })
            root.addView(bottomLaunchButton, LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                0,
                1f
            ).apply {
                topMargin = 24
                bottomMargin = 24
            })
            root.addView(bottomSkipCheckbox)
            root.addView(bottomAttributionView)
            val screen = FrameLayout(context).apply {
                setBackgroundColor(COLOR_BLACK_GREEN)
            }
            screen.addView(root, FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            ))
            screen.addView(CrtOverlayView(context), FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            ))
            setContentView(screen)
            updateState(collectState())
            updateBootControls(bootControlsReady)
        }

        fun updateState(state: WizardState) {
            if (!::bottomStatus.isInitialized || !::bottomLaunchButton.isInitialized || !::bottomSkipCheckbox.isInitialized) {
                return
            }
            bottomStatus.text = if (bootControlsReady) state.summaryLine else ""
            bottomLaunchButton.isEnabled = state.canLaunch
            bottomLaunchButton.alpha = if (state.canLaunch) 1f else 0.5f
            bottomSkipCheckbox.isChecked = skipWizardCheckbox.isChecked
            updateBootControls(bootControlsReady)
        }

        fun updateBootControls(ready: Boolean) {
            if (!::bottomCheckingView.isInitialized || !::bottomLaunchButton.isInitialized || !::bottomSkipCheckbox.isInitialized || !::bottomAttributionView.isInitialized) {
                return
            }
            bottomCheckingView.visibility = if (ready) View.GONE else View.VISIBLE
            bottomLaunchButton.visibility = if (ready) View.VISIBLE else View.GONE
            bottomSkipCheckbox.visibility = if (ready) View.VISIBLE else View.GONE
            bottomAttributionView.visibility = if (ready) View.VISIBLE else View.GONE
        }
    }

    private inner class CrtOverlayView(context: Context) : View(context) {
        private val scanlinePaint = Paint()
        private val phosphorPaint = Paint()
        private val glowPaint = Paint()
        private val noisePaint = Paint(Paint.ANTI_ALIAS_FLAG)
        private val vignettePaint = Paint(Paint.ANTI_ALIAS_FLAG)
        private var frame = 0
        private var scanlineOffset = 0f

        init {
            isClickable = false
            isFocusable = false
            importantForAccessibility = IMPORTANT_FOR_ACCESSIBILITY_NO
        }

        override fun onDraw(canvas: Canvas) {
            val viewWidth = width.toFloat()
            val viewHeight = height.toFloat()
            if (viewWidth <= 0f || viewHeight <= 0f) {
                return
            }

            var seed = System.nanoTime() xor (frame.toLong() * 1103515245L)
            val glowAlpha = 2 + nextArtifactInt(seed, 3)
            seed = advanceArtifactSeed(seed)
            glowPaint.color = Color.argb(glowAlpha, 0, 255, 102)
            canvas.drawRect(0f, 0f, viewWidth, viewHeight, glowPaint)

            scanlineOffset = if (nextArtifactInt(seed, 5) == 0) nextArtifactInt(seed, 4).toFloat() else scanlineOffset
            seed = advanceArtifactSeed(seed)
            scanlinePaint.color = Color.argb(8 + nextArtifactInt(seed, 4), 0, 0, 0)
            for (lineIndex in 0 until MAX_SCANLINES) {
                val y = lineIndex * 4f + scanlineOffset
                if (y > viewHeight) {
                    break
                }
                canvas.drawRect(0f, y, viewWidth, y + 0.65f, scanlinePaint)
            }

            seed = advanceArtifactSeed(seed)
            phosphorPaint.color = Color.argb(2 + nextArtifactInt(seed, 3), 0, 255, 102)
            for (stripeIndex in 0 until MAX_PHOSPHOR_STRIPES) {
                val x = stripeIndex * 6f
                if (x > viewWidth) {
                    break
                }
                canvas.drawRect(x, 0f, x + 1f, viewHeight, phosphorPaint)
            }

            seed = advanceArtifactSeed(seed)
            noisePaint.color = Color.argb(3 + nextArtifactInt(seed, 4), 112, 255, 164)
            val widthInt = width.coerceAtLeast(1)
            val heightInt = height.coerceAtLeast(1)
            for (speckIndex in 0 until MAX_NOISE_SPECKS) {
                seed = advanceArtifactSeed(seed)
                val x = (seed % widthInt).toFloat()
                seed = advanceArtifactSeed(seed)
                val y = (seed % heightInt).toFloat()
                canvas.drawPoint(x, y, noisePaint)
            }

            vignettePaint.shader = RadialGradient(
                viewWidth * 0.5f,
                viewHeight * 0.5f,
                maxOf(viewWidth, viewHeight) * 0.72f,
                intArrayOf(Color.TRANSPARENT, Color.argb(135, 0, 0, 0)),
                floatArrayOf(0.68f, 1f),
                Shader.TileMode.CLAMP
            )
            canvas.drawRect(0f, 0f, viewWidth, viewHeight, vignettePaint)
            vignettePaint.shader = null

            frame = (frame + 1) % CRT_FRAME_LOOP
            postInvalidateDelayed(CRT_FRAME_MS)
        }

        private fun advanceArtifactSeed(seed: Long): Long {
            return (seed * 6364136223846793005L + 1442695040888963407L) and Long.MAX_VALUE
        }

        private fun nextArtifactInt(seed: Long, bound: Int): Int {
            return (seed % bound.coerceAtLeast(1)).toInt()
        }
    }

}
