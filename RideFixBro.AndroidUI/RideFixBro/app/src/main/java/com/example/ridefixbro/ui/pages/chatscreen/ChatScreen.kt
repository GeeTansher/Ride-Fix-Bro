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
import androidx.lifecycle.viewmodel.compose.viewModel
import com.example.ridefixbro.viewmodel.ChatViewModel
import com.example.ridefixbro.viewmodel.ChatMessage
import com.mikepenz.markdown.m3.Markdown

@Composable
fun ChatScreen(viewModel: ChatViewModel = viewModel()) {
    // ViewModel se data observe kar rahe hain. Data change hoga, UI automatically update hoga!
    val messages by viewModel.messages.collectAsState()
    val isLoading by viewModel.isLoading.collectAsState()
    var inputText by remember { mutableStateOf("") }

    // Captured image ko temporarily save karne ke liye
    var capturedImage by remember { mutableStateOf<Bitmap?>(null) }

    // Camera open karne ka launcher
    val cameraLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.TakePicturePreview()
    ) { bitmap: Bitmap? ->
        capturedImage = bitmap
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
            // systemBarsPadding top/bottom notch se bachane ke liye hota hai
            .systemBarsPadding()
            // YEH LINE ADD KARNI HAI BHAi:
            .imePadding()
            .padding(16.dp)
    ) {
        // 1. Chat Messages Area (Yeh tera naya RecyclerView hai bina kisi adapter ke)
        LazyColumn(
            modifier = Modifier.weight(1f),
            reverseLayout = false
        ) {
            items(messages) { msg ->
                MessageBubble(message = msg)
                Spacer(modifier = Modifier.height(8.dp))
            }
        }

        // Agar user ne photo kheenchi hai toh ek chota sa indicator dikha do
        if (capturedImage != null) {
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
                value = inputText,
                onValueChange = { inputText = it },
                modifier = Modifier.weight(1f),
                placeholder = { Text("Photo bhej ya type kar...") },
                shape = RoundedCornerShape(24.dp)
            )
            Spacer(modifier = Modifier.width(8.dp))

            // SEND BUTTON
            IconButton(
                onClick = {
                    if (inputText.isNotBlank()) {
                        // Agar image hai toh usko Base64 banayenge, warna null
                        val base64String = capturedImage?.let { encodeBitmapToBase64(it) }

                        viewModel.sendMessage(inputText, base64String)

                        // Sab bhejne ke baad box aur image khali kar do
                        inputText = ""
                        capturedImage = null
                    }
                },
                enabled = !isLoading,
                modifier = Modifier.background(MaterialTheme.colorScheme.primary, RoundedCornerShape(50))
            ) {
                if (!isLoading){
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