package com.example.ridefixbro.ui.pages

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.example.ridefixbro.model.GarageBike
import com.example.ridefixbro.viewmodel.GarageUiState

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SelectionDropdown(label: String, selected: String, options: List<String>, enabled: Boolean, onSelect: (String) -> Unit) {
    var expanded by remember { mutableStateOf(false) }
    ExposedDropdownMenuBox(expanded = expanded && enabled, onExpandedChange = { if (enabled) expanded = it }) {
        OutlinedTextField(
            value = selected, onValueChange = {}, readOnly = true, enabled = enabled,
            label = { Text(label) }, modifier = Modifier.fillMaxWidth()
                .menuAnchor(ExposedDropdownMenuAnchorType.PrimaryNotEditable, enabled),
            trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded && enabled) }
        )
        ExposedDropdownMenu(expanded = expanded && enabled, onDismissRequest = { expanded = false }) {
            options.forEach { value ->
                DropdownMenuItem(text = { Text(value) }, onClick = { expanded = false; onSelect(value) })
            }
        }
    }
}

@Composable
fun AddBikeScreen(state: GarageUiState, enabled: Boolean, onAdd: (Int) -> Unit, onRefresh: () -> Unit) {
    var make by rememberSaveable { mutableStateOf("") }
    var model by rememberSaveable { mutableStateOf("") }
    var year by rememberSaveable { mutableStateOf("") }
    val makes = state.catalog.map { it.make }.distinct().sorted()
    val models = state.catalog.filter { it.make == make }.map { it.model }.distinct().sorted()
    val years = state.catalog.filter { it.make == make && it.model == model }
        .map { it.year }.distinct().sortedDescending().map { it.toString() }
    val selectedBike = state.catalog.singleOrNull { it.make == make && it.model == model && it.year.toString() == year }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Add new bike", style = MaterialTheme.typography.headlineMedium)
        Text("Choose a make, model and year from our catalog.")
        SelectionDropdown("Make", make, makes, enabled && !state.loading && makes.isNotEmpty()) {
            make = it; model = ""; year = ""
        }
        SelectionDropdown("Model", model, models, enabled && !state.loading && models.isNotEmpty()) {
            model = it; year = ""
        }
        SelectionDropdown("Year", year, years, enabled && !state.loading && years.isNotEmpty()) { year = it }
        Button(onClick = { selectedBike?.let { onAdd(it.id) } },
            enabled = enabled && !state.loading && selectedBike != null) { Text("Add to garage") }
        if (!state.loading && state.catalog.isEmpty()) Text("No catalog bikes available.")
        GarageStatus(state)
        TextButton(onClick = onRefresh, enabled = enabled && !state.loading) { Text("Refresh catalog") }
    }
}

@Composable
fun GarageScreen(state: GarageUiState, enabled: Boolean, onDelete: (Int) -> Unit, onRefresh: () -> Unit) {
    var pendingDelete by remember { mutableStateOf<GarageBike?>(null) }
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Your garage", style = MaterialTheme.typography.headlineMedium)
        GarageStatus(state)
        if (state.bikes.isEmpty() && !state.loading) Text("No bikes yet. Use Add new bike in the sidebar.")
        TextButton(onClick = onRefresh, enabled = enabled && !state.loading) { Text("Refresh garage") }
        LazyColumn(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(state.bikes, key = { it.id }) { bike ->
                Card(Modifier.fillMaxWidth()) {
                    Row(Modifier.padding(12.dp)) {
                        Column(Modifier.weight(1f)) {
                            Text("${bike.make} ${bike.model}", style = MaterialTheme.typography.titleMedium)
                            Text(bike.year.toString())
                        }
                        IconButton(onClick = { pendingDelete = bike }, enabled = enabled && !state.loading) {
                            Icon(Icons.Default.Delete, contentDescription = "Remove ${bike.label}")
                        }
                    }
                }
            }
        }
    }
    pendingDelete?.let { bike ->
        AlertDialog(
            onDismissRequest = { pendingDelete = null },
            title = { Text("Remove bike?") },
            text = { Text("${bike.label} will be hidden from your garage. Existing chats are not deleted.") },
            confirmButton = {
                TextButton(onClick = { pendingDelete = null; onDelete(bike.id) }, enabled = enabled && !state.loading) {
                    Text("Remove")
                }
            },
            dismissButton = { TextButton(onClick = { pendingDelete = null }) { Text("Cancel") } }
        )
    }
}

@Composable
private fun GarageStatus(state: GarageUiState) {
    if (state.loading) LinearProgressIndicator(Modifier.fillMaxWidth())
    state.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
    state.message?.let { Text(it, color = MaterialTheme.colorScheme.primary) }
}
