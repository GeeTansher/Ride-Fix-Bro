package com.example.ridefixbro.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.example.ridefixbro.model.request.ChatRequest
import com.example.ridefixbro.network.RideFixBroClient
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

// Ye data class UI pe message dikhane ke kaam aayegi
data class ChatMessage(val text: String, val isUser: Boolean)

class ChatViewModel : ViewModel() {

    // Jo messages hum UI (Compose) ko dikhayenge
    private val _messages = MutableStateFlow<List<ChatMessage>>(emptyList())
    val messages: StateFlow<List<ChatMessage>> = _messages.asStateFlow()

    // Loading spinner dikhane ke liye
    private val _isLoading = MutableStateFlow(false)
    val isLoading: StateFlow<Boolean> = _isLoading.asStateFlow()

    fun sendMessage(text: String, base64Image: String? = null) {
        // 1. User ka message list mein daalo aur UI update karo
        val currentList = _messages.value.toMutableList()
        currentList.add(ChatMessage(text = text, isUser = true))
        _messages.value = currentList

        _isLoading.value = true // Spinner chalu

        // 2. Background thread mein API call maro (taaki UI hang na ho)
        viewModelScope.launch {
            try {
                val request = ChatRequest(message = text, imageData = base64Image)

                // Tera dakiya gaya server pe... (yahan tere interface ka naam lagana agar alag ho)
                val response = RideFixBroClient.api.askMechanicBro(request)

                // 3. Bro ka reply aagaya, usko list mein daalo
                val updatedList = _messages.value.toMutableList()
                updatedList.add(ChatMessage(text = response.reply, isUser = false))
                _messages.value = updatedList

            } catch (e: Exception) {
                // Agar server band hua ya net gaya
                val errorList = _messages.value.toMutableList()
                errorList.add(ChatMessage(text = "Bhai panga ho gaya: ${e.message}", isUser = false))
                _messages.value = errorList
            } finally {
                _isLoading.value = false // Spinner band
            }
        }
    }
}