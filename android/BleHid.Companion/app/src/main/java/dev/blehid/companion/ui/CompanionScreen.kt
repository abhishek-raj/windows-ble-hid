package dev.blehid.companion.ui

import android.os.Build
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.blehid.companion.bluetooth.CompanionRepository
import dev.blehid.companion.bluetooth.CompanionState
import dev.blehid.companion.bluetooth.DevicePresence
import dev.blehid.companion.bluetooth.DiagnosticEvent
import dev.blehid.companion.bluetooth.EventLevel
import dev.blehid.companion.bluetooth.GattConnection
import dev.blehid.companion.bluetooth.HashAssessment
import dev.blehid.companion.bluetooth.ProfileConnector
import java.text.DateFormat
import java.util.Date

private val Ink = Color(0xFF15201D)
private val Canvas = Color(0xFFF4F6F5)
private val Paper = Color(0xFFFFFFFF)
private val Green = Color(0xFF176B5B)
private val GreenSoft = Color(0xFFDDEFE9)
private val Amber = Color(0xFF956314)
private val AmberSoft = Color(0xFFFFEDC8)
private val Red = Color(0xFFA33D36)
private val RedSoft = Color(0xFFFFE0DD)
private val Blue = Color(0xFF3A5E8C)
private val Border = Color(0xFFD4DBD8)
private val Muted = Color(0xFF5D6965)

@Composable
fun CompanionApp(
    repository: CompanionRepository,
    onGrantPermissions: () -> Unit,
    onAssociate: () -> Unit,
    onMonitoringChange: (Boolean) -> Unit,
    onRetryGatt: () -> Unit,
    onConnectProfiles: () -> Unit,
    onOpenBluetoothSettings: () -> Unit,
    onShareDiagnostics: () -> Unit,
) {
    val state by repository.state.collectAsStateWithLifecycle()
    BleHidTheme {
        CompanionScreen(
            state = state,
            onGrantPermissions = onGrantPermissions,
            onAssociate = onAssociate,
            onMonitoringChange = onMonitoringChange,
            onRetryGatt = onRetryGatt,
            onConnectProfiles = onConnectProfiles,
            onOpenBluetoothSettings = onOpenBluetoothSettings,
            onShareDiagnostics = onShareDiagnostics,
        )
    }
}

@Composable
private fun BleHidTheme(content: @Composable () -> Unit) {
    val colors = androidx.compose.material3.lightColorScheme(
        primary = Green,
        onPrimary = Color.White,
        secondary = Blue,
        onSecondary = Color.White,
        error = Red,
        background = Canvas,
        onBackground = Ink,
        surface = Paper,
        onSurface = Ink,
        outline = Border,
    )
    MaterialTheme(colorScheme = colors, content = content)
}

@Composable
private fun CompanionScreen(
    state: CompanionState,
    onGrantPermissions: () -> Unit,
    onAssociate: () -> Unit,
    onMonitoringChange: (Boolean) -> Unit,
    onRetryGatt: () -> Unit,
    onConnectProfiles: () -> Unit,
    onOpenBluetoothSettings: () -> Unit,
    onShareDiagnostics: () -> Unit,
) {
    Surface(color = Canvas, modifier = Modifier.fillMaxSize()) {
        LazyColumn(
            modifier = Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .navigationBarsPadding(),
        ) {
            item {
                Header(state)
                SetupActions(state, onGrantPermissions, onAssociate)
                MonitoringPanel(state, onMonitoringChange)
                DatabaseMetrics(state)
                RecoveryActions(
                    state = state,
                    onRetryGatt = onRetryGatt,
                    onConnectProfiles = onConnectProfiles,
                    onOpenBluetoothSettings = onOpenBluetoothSettings,
                )
                TimelineHeader(state.events.isNotEmpty(), onShareDiagnostics)
            }
            if (state.events.isEmpty()) {
                item {
                    Text(
                        text = "Connection events will appear here.",
                        color = Muted,
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.padding(horizontal = 20.dp, vertical = 18.dp),
                    )
                }
            } else {
                items(state.events, key = { it.id }) { event ->
                    TimelineRow(event)
                }
            }
            item { Spacer(modifier = Modifier.height(20.dp)) }
        }
    }
}

@Composable
private fun Header(state: CompanionState) {
    Column(
        modifier = Modifier.padding(start = 20.dp, top = 18.dp, end = 20.dp, bottom = 12.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = "BLEHID",
                style = MaterialTheme.typography.titleLarge,
                fontWeight = FontWeight.Black,
                color = Ink,
            )
            Spacer(modifier = Modifier.width(8.dp))
            Text(text = "/ COMPANION", color = Muted, style = MaterialTheme.typography.labelLarge)
            Spacer(modifier = Modifier.weight(1f))
            StatusPill(state.connection)
        }
        Text(
            text = state.association?.displayName ?: "No computer associated",
            style = MaterialTheme.typography.headlineSmall,
            fontWeight = FontWeight.SemiBold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        state.association?.let { association ->
            Text(
                text = association.address,
                color = Muted,
                style = MaterialTheme.typography.bodyMedium,
            )
        }
    }
}

@Composable
private fun SetupActions(
    state: CompanionState,
    onGrantPermissions: () -> Unit,
    onAssociate: () -> Unit,
) {
    if (state.permissionsGranted && state.association != null) return
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp, vertical = 6.dp),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        if (!state.permissionsGranted) {
            Button(onClick = onGrantPermissions, modifier = Modifier.weight(1f)) {
                Text("Allow nearby devices")
            }
        }
        OutlinedButton(onClick = onAssociate, modifier = Modifier.weight(1f)) {
            Text(if (state.association == null) "Find computer" else "Change computer")
        }
    }
}

@Composable
private fun MonitoringPanel(state: CompanionState, onMonitoringChange: (Boolean) -> Unit) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp, vertical = 10.dp),
        shape = RoundedCornerShape(8.dp),
        colors = CardDefaults.cardColors(containerColor = Paper),
        border = androidx.compose.foundation.BorderStroke(1.dp, Border),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text("Connection monitor", fontWeight = FontWeight.SemiBold)
                Text(
                    text = connectionDescription(state),
                    color = Muted,
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(top = 4.dp),
                )
            }
            Switch(
                checked = state.monitoringEnabled,
                onCheckedChange = onMonitoringChange,
                enabled = state.association != null,
                colors = SwitchDefaults.colors(checkedTrackColor = Green),
            )
        }
    }
}

@Composable
private fun DatabaseMetrics(state: CompanionState) {
    Text(
        text = "GATT DATABASE",
        style = MaterialTheme.typography.labelMedium,
        fontWeight = FontWeight.Bold,
        color = Muted,
        modifier = Modifier.padding(start = 20.dp, top = 14.dp, bottom = 8.dp),
    )
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Metric(
            label = "HID",
            value = if (state.database.hasHidService) "Present" else "Unknown",
            accent = if (state.database.hasHidService) Green else Muted,
            modifier = Modifier.weight(1f),
        )
        Metric(
            label = "Reports",
            value = state.database.hidReportCount.takeIf { it > 0 }?.toString() ?: "-",
            accent = Blue,
            modifier = Modifier.weight(1f),
        )
        Metric(
            label = "Hash",
            value = hashLabel(state.database.hashAssessment),
            accent = hashColor(state.database.hashAssessment),
            modifier = Modifier.weight(1f),
        )
    }
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Metric(
            label = "Services",
            value = state.database.serviceCount.takeIf { it > 0 }?.toString() ?: "-",
            accent = Blue,
            modifier = Modifier.weight(1f),
        )
        Metric(
            label = "Changed",
            value = state.database.serviceChangedCount.toString(),
            accent = if (state.database.serviceChangedCount > 0) Amber else Muted,
            modifier = Modifier.weight(1f),
        )
        Metric(
            label = "Presence",
            value = presenceLabel(state.presence),
            accent = if (state.presence == DevicePresence.Present) Green else Muted,
            modifier = Modifier.weight(1f),
        )
    }
}

@Composable
private fun Metric(label: String, value: String, accent: Color, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .background(Paper, RoundedCornerShape(6.dp))
            .padding(horizontal = 12.dp, vertical = 11.dp),
    ) {
        Text(text = label, color = Muted, style = MaterialTheme.typography.labelSmall)
        Text(
            text = value,
            color = accent,
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.Bold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(top = 2.dp),
        )
    }
}

@Composable
private fun RecoveryActions(
    state: CompanionState,
    onRetryGatt: () -> Unit,
    onConnectProfiles: () -> Unit,
    onOpenBluetoothSettings: () -> Unit,
) {
    Text(
        text = "RECOVERY",
        style = MaterialTheme.typography.labelMedium,
        fontWeight = FontWeight.Bold,
        color = Muted,
        modifier = Modifier.padding(start = 20.dp, top = 16.dp, bottom = 8.dp),
    )
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        OutlinedButton(
            onClick = onRetryGatt,
            enabled = state.monitoringEnabled,
            modifier = Modifier.weight(1f),
        ) {
            Text("Retry GATT")
        }
        Button(
            onClick = onConnectProfiles,
            enabled = state.association != null,
            colors = ButtonDefaults.buttonColors(containerColor = Blue),
            modifier = Modifier.weight(1f),
        ) {
            Text(if (ProfileConnector.isSupported()) "Connect input" else "Bluetooth settings")
        }
    }
    if (Build.VERSION.SDK_INT < ProfileConnector.PROFILE_CONNECT_API_LEVEL) {
        Text(
            text = "Android ${Build.VERSION.RELEASE} requires system settings to reconnect the input profile.",
            color = Muted,
            style = MaterialTheme.typography.bodySmall,
            modifier = Modifier.padding(horizontal = 20.dp, vertical = 8.dp),
        )
    } else {
        OutlinedButton(
            onClick = onOpenBluetoothSettings,
            modifier = Modifier.padding(horizontal = 20.dp, vertical = 8.dp),
        ) {
            Text("Open Bluetooth settings")
        }
    }
}

@Composable
private fun TimelineHeader(hasEvents: Boolean, onShareDiagnostics: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(start = 20.dp, top = 18.dp, end = 20.dp, bottom = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = "EVENTS",
            style = MaterialTheme.typography.labelMedium,
            fontWeight = FontWeight.Bold,
            color = Muted,
        )
        Spacer(modifier = Modifier.weight(1f))
        if (hasEvents) {
            OutlinedButton(onClick = onShareDiagnostics) { Text("Share diagnostics") }
        }
    }
}

@Composable
private fun TimelineRow(event: DiagnosticEvent) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp, vertical = 10.dp),
        verticalAlignment = Alignment.Top,
    ) {
        Box(
            modifier = Modifier
                .padding(top = 5.dp)
                .size(8.dp)
                .background(eventColor(event.level), CircleShape),
        )
        Spacer(modifier = Modifier.width(12.dp))
        Column(modifier = Modifier.weight(1f)) {
            Text(text = event.message, style = MaterialTheme.typography.bodyMedium)
            Text(
                text = DateFormat.getTimeInstance(DateFormat.MEDIUM).format(Date(event.timestampMillis)),
                color = Muted,
                style = MaterialTheme.typography.labelSmall,
                modifier = Modifier.padding(top = 3.dp),
            )
        }
    }
    HorizontalDivider(color = Border, modifier = Modifier.padding(horizontal = 20.dp))
}

@Composable
private fun StatusPill(connection: GattConnection) {
    val (background, foreground) = when (connection) {
        GattConnection.Ready -> GreenSoft to Green
        GattConnection.Failed -> RedSoft to Red
        GattConnection.WaitingToRetry -> AmberSoft to Amber
        GattConnection.Connecting, GattConnection.Discovering -> Color(0xFFDCE7F5) to Blue
        GattConnection.Idle -> Color(0xFFE6EAE8) to Muted
    }
    Text(
        text = connectionLabel(connection),
        color = foreground,
        style = MaterialTheme.typography.labelMedium,
        fontWeight = FontWeight.Bold,
        modifier = Modifier
            .background(background, RoundedCornerShape(50))
            .padding(horizontal = 10.dp, vertical = 6.dp),
    )
}

private fun connectionDescription(state: CompanionState): String = when (state.connection) {
    GattConnection.Idle -> if (state.monitoringEnabled) "Waiting for the associated computer" else "Off"
    GattConnection.Connecting -> "Opening a diagnostic GATT connection"
    GattConnection.Discovering -> "Reading the remote service database"
    GattConnection.Ready -> "Connected and watching database changes"
    GattConnection.WaitingToRetry -> "Retrying in ${state.retryInSeconds ?: 0} seconds"
    GattConnection.Failed -> state.lastError ?: "Connection failed"
}

private fun connectionLabel(connection: GattConnection): String = when (connection) {
    GattConnection.Idle -> "IDLE"
    GattConnection.Connecting -> "CONNECTING"
    GattConnection.Discovering -> "DISCOVERING"
    GattConnection.Ready -> "READY"
    GattConnection.WaitingToRetry -> "RETRYING"
    GattConnection.Failed -> "FAILED"
}

private fun hashLabel(assessment: HashAssessment): String = when (assessment) {
    HashAssessment.Unavailable -> "-"
    HashAssessment.FirstSeen -> "Saved"
    HashAssessment.Unchanged -> "Same"
    HashAssessment.Changed -> "Changed"
}

private fun hashColor(assessment: HashAssessment): Color = when (assessment) {
    HashAssessment.FirstSeen, HashAssessment.Unchanged -> Green
    HashAssessment.Changed -> Amber
    HashAssessment.Unavailable -> Muted
}

private fun presenceLabel(presence: DevicePresence): String = when (presence) {
    DevicePresence.Unknown -> "Unknown"
    DevicePresence.Present -> "Nearby"
    DevicePresence.Away -> "Away"
}

private fun eventColor(level: EventLevel): Color = when (level) {
    EventLevel.Info -> Green
    EventLevel.Warning -> Amber
    EventLevel.Error -> Red
}