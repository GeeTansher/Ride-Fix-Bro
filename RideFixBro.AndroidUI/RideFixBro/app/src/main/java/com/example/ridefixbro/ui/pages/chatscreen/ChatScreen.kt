package com.example.ridefixbro.ui.pages.chatscreen

import android.Manifest
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CameraAlt
import androidx.compose.material.icons.filled.Send
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.example.ridefixbro.viewmodel.ChatViewModel
import com.example.ridefixbro.viewmodel.ChatMessage
import com.mikepenz.markdown.m3.Markdown
import com.example.ridefixbro.model.GarageBike
import com.example.ridefixbro.ui.pages.SelectionDropdown

@Composable
fun ChatScreen(viewModel: ChatViewModel, userId: String, enabled: Boolean, garage: List<GarageBike>) {
    // ViewModel se data observe kar rahe hain. Data change hoga, UI automatically update hoga!
    val state by viewModel.state.collectAsState()

    // Camera open karne ka launcher
    val cameraLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.TakePicturePreview()
    ) { bitmap: Bitmap? ->
        if (bitmap != null) viewModel.attachPhoto(userId, encodeBitmapToBase64(bitmap))
    }

    // context variable
    val context = LocalContext.current

    // Permission mangne wala
    val permissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestPermission()
    ) { isGranted: Boolean ->
        if (isGranted) {
            // User ne 'Allow' daba diya, ab camera khol do!
            cameraLauncher.launch(null)
        } else {
            // User ne 'Deny' kar diya
            Toast.makeText(context, "Bhai camera ki permission toh de de!", Toast.LENGTH_SHORT).show()
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .imePadding()
            .padding(16.dp)
    ) {
        SelectionDropdown(
            if (state.selectionLocked) "Chat selection (locked)" else "Select General or a bike",
            if (state.isGeneral) "General" else state.selectedBike?.label ?: "",
            listOf("General") + garage.map { it.label },
            enabled && !state.loading && !state.selectionLocked
        ) { label ->
            if (label == "General") {
                viewModel.selectGeneral()
            } else {
                garage.firstOrNull { it.label == label }?.let { viewModel.selectBike(it.id) }
            }
        }
        if (state.isGeneral) {
            Text("General chat: No manual-backed or bike-specific details are guaranteed.",
                style = MaterialTheme.typography.bodySmall)
        }
        if (state.selectionLocked) Text("Selection locked. Change karne ke liye New Chat kholo.",
            style = MaterialTheme.typography.bodySmall)
        state.selectionError?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        Spacer(Modifier.height(8.dp))
        if (!state.hasSelection) {
            if (state.loading) LinearProgressIndicator(Modifier.fillMaxWidth())
            Text("Choose General or a bike to start chatting.")
            if (garage.isEmpty()) Text("You can add bikes from the sidebar.", style = MaterialTheme.typography.bodySmall)
            return@Column
        }
        // 1. Chat Messages Area (Yeh tera naya RecyclerView hai bina kisi adapter ke)
        LazyColumn(
            modifier = Modifier.weight(1f),
            reverseLayout = false
        ) {
            if (state.hasOlderMessages) item {
                TextButton(onClick = viewModel::loadOlderMessages, enabled = enabled && !state.loading) { Text("Load older messages") }
            }
            items(state.messages) { msg ->
                if (msg.photoNotStored) Text("Photo is not stored. Reattach it if needed.", style = MaterialTheme.typography.bodySmall)
                MessageBubble(message = msg)
                Spacer(modifier = Modifier.height(8.dp))
            }
        }

        // Agar user ne photo kheenchi hai toh ek chota sa indicator dikha do
        if (state.draft.imageData != null) {
            Text("📸 Photo ready! Message type kar aur bhej de...", color = MaterialTheme.colorScheme.primary)
            Spacer(modifier = Modifier.height(4.dp))
        }

        // 2. Text Input, camera Button aur Send Button with progress bar.
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically
        ) {
            // CAMERA BUTTON
            IconButton(
                enabled = enabled && !state.loading,
                onClick = {
                    // Pehle check kar ki kya apne paas permission pehle se hai?
                    val hasPermission = ContextCompat.checkSelfPermission(
                        context,
                        Manifest.permission.CAMERA
                    ) == PackageManager.PERMISSION_GRANTED

                    if (hasPermission) {
                        // Permission hai, seedha camera khol
                        cameraLauncher.launch(null)
                    } else {
                        // Permission nahi hai, popup fek ke maang
                        permissionLauncher.launch(Manifest.permission.CAMERA)
                    }
                }
            ) {
                Icon(Icons.Filled.CameraAlt, contentDescription = "Camera", tint = MaterialTheme.colorScheme.primary)
            }

            OutlinedTextField(
                value = state.draft.text,
                onValueChange = { viewModel.updateDraftText(it) },
                modifier = Modifier.weight(1f),
                enabled = enabled,
                placeholder = { Text("Photo bhej ya type kar...") },
                shape = RoundedCornerShape(24.dp)
            )
            Spacer(modifier = Modifier.width(8.dp))

            // SEND BUTTON
            IconButton(
                onClick = {
                    viewModel.sendDraft()
                },
                enabled = enabled && !state.loading && state.hasSelection && state.draft.text.isNotBlank(),
                modifier = Modifier.background(MaterialTheme.colorScheme.primary, RoundedCornerShape(50))
            ) {
                if (!state.loading){
                    Icon(Icons.Filled.Send, contentDescription = "Send", tint = Color.White)
                }
                else{
                    CircularProgressIndicator(color = Color.White)
                }
            }
        }
    }
}

// Ek chota sa helper function messages ko left-right dikhane ke liye (WhatsApp style)
@Composable
fun MessageBubble(message: ChatMessage) {
    val isUser = message.isUser
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = if (isUser) Arrangement.End else Arrangement.Start
    ) {
        Box(
            modifier = Modifier
                .background(
                    color = if (isUser) MaterialTheme.colorScheme.primary else Color.DarkGray,
                    shape = RoundedCornerShape(16.dp)
                )
                .padding(12.dp)
                .widthIn(max = 280.dp)
        ) {
            if (isUser) {
                // User ka message normal text mein dikha
                Text(
                    text = message.text,
                    color = Color.White
                )
            } else {
                // Bro ka message Markdown (Rich Text) mein dikha
                Markdown(
                    content = message.text
                )
            }
        }
    }
}