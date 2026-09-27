package com.example.ridefixbro.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.example.ridefixbro.model.request.ChatRequest
import com.example.ridefixbro.network.RideFixBroClient
import com.example.ridefixbro.auth.AuthRepository
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import retrofit2.HttpException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.util.UUID

// Ye data class UI pe message dikhane ke kaam aayegi
data class ChatMessage(val text: String, val isUser: Boolean)

class ChatViewModel(private val auth: AuthRepository) : ViewModel() {

    private var sessionId = UUID.randomUUID().toString()
    private var activeUserId: String? = null
    private var sendJob: Job? = null

    // Jo messages hum UI (Compose) ko dikhayenge
    private val _messages = MutableStateFlow<List<ChatMessage>>(emptyList())
    val messages: StateFlow<List<ChatMessage>> = _messages.asStateFlow()

    // Loading spinner dikhane ke liye
    private val _isLoading = MutableStateFlow(false)
    val isLoading: StateFlow<Boolean> = _isLoading.asStateFlow()

    init {
        viewModelScope.launch {
            auth.userId.collect { userId ->
                if (activeUserId != userId) {
                    sendJob?.cancel()
                    activeUserId = userId
                    sessionId = UUID.randomUUID().toString()
                    _messages.value = emptyList()
                    _isLoading.value = false
                }
            }
        }
    }

    fun sendMessage(text: String, base64Image: String? = null) {
        val userId = auth.userId.value ?: return
        if (_isLoading.value || text.isBlank()) return
        // 1. User ka message list mein daalo aur UI update karo
        val currentList = _messages.value.toMutableList()
        currentList.add(ChatMessage(text = text, isUser = true))
        _messages.value = currentList

        _isLoading.value = true // Spinner chalu

        // 2. Background thread mein API call maro (taaki UI hang na ho)
        sendJob = viewModelScope.launch {
            try {
                val request = ChatRequest(sessionId = sessionId, message = text, imageData = base64Image)

                // Tera dakiya gaya server pe... (yahan tere interface ka naam lagana agar alag ho)
                val token = auth.accessToken(userId)
                val response = RideFixBroClient.api.askMechanicBro(request, "Bearer $token")
                currentCoroutineContext().ensureActive()
                if (auth.userId.value != userId) return@launch

                // 3. Bro ka reply aagaya, usko list mein daalo
                val updatedList = _messages.value.toMutableList()
                updatedList.add(ChatMessage(text = response.reply, isUser = false))
                _messages.value = updatedList

            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                if (e is HttpException && e.code() == 401) auth.invalidateSession(userId)
                if (auth.userId.value != userId) return@launch
                // Agar server band hua ya net gaya
                val errorList = _messages.value.toMutableList()
                val errorText = when ((e as? HttpException)?.code()) {
                    400 -> "Message ya image valid nahi hai. Chhota message/valid photo bhej."
                    413 -> "Photo/request bahut badi hai. Compress karke bhej."
                    422 -> "Tool budget khatam ho gaya. Sawal thoda chhota kar."
                    429 -> "Abhi requests ki limit aa gayi. Thoda ruk ke retry kar."
                    503 -> "Account database abhi available nahi hai. Baad mein retry kar."
                    else -> "Answer nahi aa paaya. Network check karke retry kar."
                }
                errorList.add(ChatMessage(text = "Bhai, $errorText", isUser = false))
                _messages.value = errorList
            } finally {
                if (auth.userId.value == userId) _isLoading.value = false
            }
        }
    }
}