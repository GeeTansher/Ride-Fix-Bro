package com.example.ridefixbro.ui.pages

import android.net.Uri
import android.provider.OpenableColumns
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.example.ridefixbro.model.response.UserProfileResponse
import com.example.ridefixbro.network.PdfRequestBody
import com.example.ridefixbro.viewmodel.AdminBikeViewModel
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.MultipartBody

@Composable
fun AdminBikeScreen(profile: UserProfileResponse, viewModel: AdminBikeViewModel, enabled: Boolean) {
    if (profile.role != "Admin") {
        Text("Admin access required.", Modifier.padding(16.dp))
        return
    }
    val state by viewModel.state.collectAsState()
    val resolver = LocalContext.current.applicationContext.contentResolver
    val scope = rememberCoroutineScope()
    var confirm by remember { mutableStateOf(false) }
    var selectionGeneration by remember { mutableIntStateOf(0) }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) {
            val requestGeneration = ++selectionGeneration
            scope.launch {
                try {
                    val name = withContext(Dispatchers.IO) {
                        resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
                            if (cursor.moveToFirst()) cursor.getString(0) else null
                        } ?: "manual.pdf"
                    }
                    if (selectionGeneration == requestGeneration)
                        viewModel.selectPdf(profile.supabaseUserId, uri.toString(), name)
                } catch (error: CancellationException) {
                    throw error
                } catch (error: Exception) {
                    if (selectionGeneration == requestGeneration) viewModel.fileError(profile.supabaseUserId)
                }
            }
        }
    }
    val editable = enabled && !state.loading
    val form = state.form
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Publish bike & official manual", style = MaterialTheme.typography.headlineSmall)
        Text("All bike fields and a text-based PDF are required. The bike is added to the catalog only after publication succeeds.")
        OutlinedTextField(form.make, { viewModel.updateForm(form.copy(make = it)) },
            label = { Text("Make") }, enabled = editable, singleLine = true, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(form.model, { viewModel.updateForm(form.copy(model = it)) },
            label = { Text("Model") }, enabled = editable, singleLine = true, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(form.year, { viewModel.updateForm(form.copy(year = it)) },
            label = { Text("Year") }, enabled = editable, singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number))
        OutlinedTextField(form.manualKey, { viewModel.updateForm(form.copy(manualKey = it)) },
            label = { Text("ManualKey") }, enabled = editable, singleLine = true, modifier = Modifier.fillMaxWidth())
        Text("Re-upload: use the same make, model, year and ManualKey. Do not reuse another bike's key.")
        OutlinedTextField(form.skipPages, { viewModel.updateForm(form.copy(skipPages = it)) },
            label = { Text("Starting pages to skip") }, enabled = editable, singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number))
        Text("0 starts at page 1; 7 starts at physical PDF page 8. Maximum PDF size: 20 MiB.")
        OutlinedButton(onClick = { picker.launch(arrayOf("application/pdf")) }, enabled = editable) {
            Text(if (state.pdfUri == null) "Select PDF" else "Change PDF")
        }
        state.pdfName?.let { Text(it) }
        Button(onClick = { confirm = true }, enabled = editable && form.isValid && state.pdfUri != null) {
            Text("Publish official manual")
        }
        if (state.loading) {
            LinearProgressIndicator(Modifier.fillMaxWidth())
            Text("Uploading and processing the PDF. Wait for confirmation; large manuals can take time.")
        }
        state.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        state.published?.let {
            Text("Published ${it.bike.make} ${it.bike.model} (${it.bike.year}): ${it.chunks} chunks.",
                color = MaterialTheme.colorScheme.primary)
            Text("The bike is available in the catalog. Add it to your own garage separately.")
        }
        Text("Official collection is configured on the backend using Qdrant_Vector_DB:ManualCollection.",
            style = MaterialTheme.typography.bodySmall)
    }
    if (confirm) AlertDialog(
        onDismissRequest = { confirm = false },
        title = { Text("Publish this manual?") },
        text = { Text("Existing official content for '${form.manualKey}' will be replaced. Only upload a manual applicable to ${form.make} ${form.model} ${form.year}.") },
        confirmButton = {
            TextButton(enabled = editable, onClick = {
                confirm = false
                val file = state.pdfUri?.let { MultipartBody.Part.createFormData(
                    "file", state.pdfName ?: "manual.pdf", PdfRequestBody(resolver, Uri.parse(it))) }
                viewModel.publish(profile, file)
            }) { Text("Publish") }
        },
        dismissButton = { TextButton(onClick = { confirm = false }) { Text("Cancel") } }
    )
}
