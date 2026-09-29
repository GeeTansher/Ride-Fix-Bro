package com.example.ridefixbro.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.example.ridefixbro.model.request.ChatRequest
import com.example.ridefixbro.network.RideFixBroClient
import com.example.ridefixbro.auth.AuthRepository
import com.example.ridefixbro.auth.SessionNotReadyException
import com.example.ridefixbro.network.RideFixApiInterface
import io.github.jan.supabase.auth.status.SessionStatus
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
import java.io.InterruptedIOException

// Ye data class UI pe message dikhane ke kaam aayegi
data class ChatMessage(val text: String, val isUser: Boolean)
data class ChatDraft(val text: String = "", val imageData: String? = null)

class ChatViewModel(
    private val auth: AuthRepository,
    private val api: RideFixApiInterface = RideFixBroClient.chatApi
) : ViewModel() {

    private var sessionId = UUID.randomUUID().toString()
    private var activeUserId: String? = null
    private var sendJob: Job? = null
    private var sendGeneration = 0
    private var sessionReady = false
    private val _draft = MutableStateFlow(ChatDraft())
    val draft = _draft.asStateFlow()

    // Jo messages hum UI (Compose) ko dikhayenge
    private val _messages = MutableStateFlow<List<ChatMessage>>(emptyList())
    val messages: StateFlow<List<ChatMessage>> = _messages.asStateFlow()

    // Loading spinner dikhane ke liye
    private val _isLoading = MutableStateFlow(false)
    val isLoading: StateFlow<Boolean> = _isLoading.asStateFlow()

    init {
        viewModelScope.launch {
            try {
                auth.sessionStatus.collect { status ->
                    when (status) {
                        is SessionStatus.Authenticated -> {
                            changeAccount(status.session.user?.id)
                            sessionReady = true
                        }
                        is SessionStatus.NotAuthenticated -> {
                            sessionReady = false
                            changeAccount(null)
                        }
                        else -> {
                            // Background/temporary refresh failure logout nahi hai. Chat history rakho.
                            sendJob?.cancel()
                            sessionReady = false
                            _isLoading.value = false
                        }
                    }
                }
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // Auth screen setup/restore error dikhayegi; chat kisi purane account par nahi chalegi.
                changeAccount(null)
                sessionReady = false
            }
        }
    }

    private fun changeAccount(userId: String?) {
        if (activeUserId == userId) return
        sendJob?.cancel()
        activeUserId = userId
        sessionId = UUID.randomUUID().toString()
        _messages.value = emptyList()
        _draft.value = ChatDraft()
        _isLoading.value = false
    }

    fun updateDraftText(text: String) {
        _draft.value = _draft.value.copy(text = text)
    }

    fun attachPhoto(userId: String, imageData: String?) {
        if (activeUserId == userId) {
            _draft.value = _draft.value.copy(imageData = imageData)
        }
    }

    fun sendDraft() {
        sendMessage(_draft.value.text, _draft.value.imageData)
    }

    fun sendMessage(text: String, base64Image: String? = null) {
        val userId = activeUserId ?: return
        if (!sessionReady || _isLoading.value || text.isBlank()) return
        _draft.value = ChatDraft()
        // 1. User ka message list mein daalo aur UI update karo
        val currentList = _messages.value.toMutableList()
        currentList.add(ChatMessage(text = text, isUser = true))
        _messages.value = currentList

        _isLoading.value = true // Spinner chalu
        val requestGeneration = ++sendGeneration
        val requestSessionId = sessionId

        // 2. Background thread mein API call maro (taaki UI hang na ho)
        sendJob = viewModelScope.launch {
            var token: String? = null
            try {
                val request = ChatRequest(sessionId = requestSessionId, message = text, imageData = base64Image)

                // Tera dakiya gaya server pe... (yahan tere interface ka naam lagana agar alag ho)
                token = auth.accessToken(userId)
                val response = api.askMechanicBro(request, "Bearer $token")
                currentCoroutineContext().ensureActive()
                if (activeUserId != userId || sendGeneration != requestGeneration) return@launch

                // 3. Bro ka reply aagaya, usko list mein daalo
                val updatedList = _messages.value.toMutableList()
                updatedList.add(ChatMessage(text = response.reply, isUser = false))
                _messages.value = updatedList

            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                if (e is HttpException && e.code() == 401 && token != null) auth.invalidateSession(userId, token)
                if (activeUserId != userId || sendGeneration != requestGeneration) return@launch
                // Agar server band hua ya net gaya
                val errorList = _messages.value.toMutableList()
                val errorText = if (e is InterruptedIOException) {
                    "Response ka wait time khatam ho gaya. Dobara try kar."
                } else if (e is SessionNotReadyException) {
                    "Session refresh chal raha hai. Thodi der mein retry kar."
                } else when ((e as? HttpException)?.code()) {
                    400 -> "Message ya image valid nahi hai. Chhota message/valid photo bhej."
                    413 -> "Photo/request bahut badi hai. Compress karke bhej."
                    422 -> "Tool budget khatam ho gaya. Sawal thoda chhota kar."
                    429 -> "Abhi requests ki limit aa gayi. Thoda ruk ke retry kar."
                    503 -> "Account database abhi available nahi hai. Baad mein retry kar."
                    504 -> "Server ka response time par nahi aaya. Dobara try kar."
                    else -> "Answer nahi aa paaya. Network check karke retry kar."
                }
                errorList.add(ChatMessage(text = "Bhai, $errorText", isUser = false))
                _messages.value = errorList
            } finally {
                if (activeUserId == userId && sendGeneration == requestGeneration) _isLoading.value = false
            }
        }
    }
}