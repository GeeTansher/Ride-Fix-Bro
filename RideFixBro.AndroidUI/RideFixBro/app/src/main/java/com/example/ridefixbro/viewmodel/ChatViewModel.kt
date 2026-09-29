package com.example.ridefixbro.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.example.ridefixbro.auth.AuthRepository
import com.example.ridefixbro.auth.SessionNotReadyException
import com.example.ridefixbro.model.GarageBike
import com.example.ridefixbro.model.request.ChatRequest
import com.example.ridefixbro.model.response.ChatErrorResponse
import com.example.ridefixbro.network.RideFixApiInterface
import com.example.ridefixbro.network.RideFixBroClient
import com.google.gson.Gson
import com.google.gson.JsonParseException
import io.github.jan.supabase.auth.status.SessionStatus
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import retrofit2.HttpException
import java.io.IOException
import java.io.InterruptedIOException

data class ChatMessage(val text: String, val isUser: Boolean, val photoNotStored: Boolean = false)
data class ChatDraft(val text: String = "", val imageData: String? = null)
data class RecentChat(val id: Int, val title: String, val bikeLabel: String?)
data class ChatUiState(
    val chatId: Int? = null,
    val selectedBike: GarageBike? = null,
    val isGeneral: Boolean = false,
    val selectionLocked: Boolean = false,
    val messages: List<ChatMessage> = emptyList(),
    val draft: ChatDraft = ChatDraft(),
    val loading: Boolean = false,
    val selectionError: String? = null,
    val nextBeforeSequence: Int? = null,
    val recentChats: List<RecentChat> = emptyList(),
    val recentLoading: Boolean = false,
    val historyError: String? = null,
    val moreChats: Boolean = false
) {
    val hasSelection: Boolean get() = isGeneral || selectedBike != null
    val hasOlderMessages: Boolean get() = nextBeforeSequence != null
}

class ChatViewModel(
    private val auth: AuthRepository,
    private val api: RideFixApiInterface = RideFixBroClient.chatApi,
    private val sessionApi: RideFixApiInterface = RideFixBroClient.api
) : ViewModel() {
    private val _state = MutableStateFlow(ChatUiState())
    val state = _state.asStateFlow()
    private var activeUserId: String? = null
    private var sessionReady = false
    private var generation = 0
    private var recentGeneration = 0
    private var chatJob: Job? = null
    private var recentJob: Job? = null
    private var garage: List<GarageBike> = emptyList()
    private val drafts = mutableMapOf<Int, ChatDraft>()

    init {
        viewModelScope.launch {
            try {
                auth.sessionStatus.collect { status ->
                    when (status) {
                        is SessionStatus.Authenticated -> {
                            val refresh = !sessionReady || activeUserId != status.session.user?.id
                            changeAccount(status.session.user?.id)
                            sessionReady = true
                            if (refresh) refreshRecent()
                        }
                        is SessionStatus.NotAuthenticated -> {
                            sessionReady = false
                            changeAccount(null)
                        }
                        else -> {
                            // Temporary auth refresh is not logout; preserve the visible chat and draft.
                            sessionReady = false
                            chatJob?.cancel()
                            recentJob?.cancel()
                            recentGeneration++
                            _state.update { it.copy(loading = false, recentLoading = false) }
                        }
                    }
                }
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                changeAccount(null)
                sessionReady = false
                _state.update { it.copy(historyError = "Session unavailable. Sign in again.") }
            }
        }
    }

    private fun changeAccount(userId: String?) {
        if (activeUserId == userId) return
        chatJob?.cancel()
        recentJob?.cancel()
        generation++
        recentGeneration++
        activeUserId = userId
        garage = emptyList()
        drafts.clear()
        _state.value = ChatUiState()
    }

    private fun resetCurrentChat() {
        generation++
        _state.update {
            ChatUiState(recentChats = it.recentChats, recentLoading = it.recentLoading,
                historyError = it.historyError, moreChats = it.moreChats)
        }
    }

    private fun saveDraft() {
        val current = _state.value
        current.chatId?.let { drafts[it] = current.draft }
    }

    private fun isCurrent(userId: String, requestGeneration: Int) =
        activeUserId == userId && generation == requestGeneration

    fun updateGarage(bikes: List<GarageBike>) {
        garage = bikes
        _state.update { current ->
            if (!current.selectionLocked && bikes.none { it.id == current.selectedBike?.id })
                current.copy(selectedBike = null) else current
        }
    }

    fun selectBike(id: Int) {
        if (_state.value.selectionLocked) {
            _state.update { it.copy(selectionError = "Chat selection locked hai. New Chat kholo.") }
            return
        }
        val bike = garage.find { it.id == id }
        if (bike == null) {
            _state.update { it.copy(selectionError = "Bike apni garage se select kar.") }
            return
        }
        _state.update { it.copy(selectedBike = bike, isGeneral = false, selectionError = null) }
    }

    fun selectGeneral() {
        if (_state.value.selectionLocked) {
            _state.update { it.copy(selectionError = "Chat selection locked hai. New Chat kholo.") }
            return
        }
        _state.update { it.copy(selectedBike = null, isGeneral = true, selectionError = null) }
    }

    fun newChat() {
        saveDraft()
        chatJob?.cancel()
        resetCurrentChat()
    }

    fun refreshRecent(loadMore: Boolean = false) {
        val userId = activeUserId ?: return
        val current = _state.value
        if (!sessionReady || (loadMore && (current.recentLoading || !current.moreChats))) return
        recentJob?.cancel()
        val requestGeneration = ++recentGeneration
        val before = if (loadMore) current.recentChats.lastOrNull()?.id else null
        recentJob = viewModelScope.launch {
            _state.update { it.copy(recentLoading = true, historyError = null) }
            var token: String? = null
            try {
                token = auth.accessToken(userId)
                val rows = sessionApi.chats("Bearer $token", before)
                currentCoroutineContext().ensureActive()
                if (activeUserId != userId || recentGeneration != requestGeneration) return@launch
                val page = rows.map { RecentChat(it.id, it.title, if (it.isGeneral) "General" else it.bike?.label) }
                _state.update { it.copy(
                    recentChats = if (loadMore) (it.recentChats + page).distinctBy { chat -> chat.id } else page,
                    moreChats = rows.size == 50) }
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                if (error is HttpException && error.code() == 401 && token != null) auth.invalidateSession(userId, token)
                if (activeUserId == userId && recentGeneration == requestGeneration)
                    _state.update { it.copy(historyError = "Saved chats load nahi hue. Retry karo.") }
            } finally {
                if (activeUserId == userId && recentGeneration == requestGeneration)
                    _state.update { it.copy(recentLoading = false) }
            }
        }
    }

    fun openChat(id: Int) {
        if (id <= 0) {
            _state.update { it.copy(selectionError = "Invalid saved chat.") }
            return
        }
        val userId = activeUserId ?: return
        if (!sessionReady) return
        saveDraft()
        chatJob?.cancel()
        resetCurrentChat()
        _state.update { it.copy(chatId = id, selectionLocked = true, draft = drafts[id] ?: ChatDraft()) }
        fetchMessages(userId, id, null)
    }

    fun loadOlderMessages() {
        val userId = activeUserId ?: return
        val current = _state.value
        val id = current.chatId ?: return
        val before = current.nextBeforeSequence ?: return
        if (sessionReady && !current.loading) fetchMessages(userId, id, before)
    }

    private fun fetchMessages(userId: String, id: Int, before: Int?) {
        val requestGeneration = generation
        _state.update { it.copy(loading = true) }
        chatJob = viewModelScope.launch {
            var token: String? = null
            try {
                token = auth.accessToken(userId)
                val detail = sessionApi.chat(id, "Bearer $token", before)
                currentCoroutineContext().ensureActive()
                if (!isCurrent(userId, requestGeneration)) return@launch
                val page = detail.messages.map { ChatMessage(it.text, it.isUser, it.photoNotStored) }
                _state.update { it.copy(selectedBike = detail.chat.bike, isGeneral = detail.chat.isGeneral,
                    messages = if (before == null) page else page + it.messages,
                    nextBeforeSequence = detail.nextBeforeSequence, selectionError = null) }
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                if (error is HttpException && error.code() == 401 && token != null) auth.invalidateSession(userId, token)
                if (isCurrent(userId, requestGeneration))
                    _state.update { it.copy(selectionError = "Chat load nahi hui. Recent chats se dobara kholo.") }
            } finally {
                if (isCurrent(userId, requestGeneration)) _state.update { it.copy(loading = false) }
            }
        }
    }

    fun updateDraftText(text: String) { _state.update { it.copy(draft = it.draft.copy(text = text)) } }

    fun attachPhoto(userId: String, imageData: String?) {
        if (activeUserId == userId && _state.value.hasSelection)
            _state.update { it.copy(draft = it.draft.copy(imageData = imageData)) }
    }

    fun sendDraft() = sendMessage(_state.value.draft.text, _state.value.draft.imageData)

    private fun rememberSessionId(id: Int, requestId: Int) {
        if (id <= 0 || (requestId > 0 && requestId != id))
            throw IOException("Invalid chat ID in the server response. Reload saved chats.")
        _state.update { it.copy(chatId = id) }
    }

    private fun rememberFailureSessionId(error: HttpException, requestId: Int) {
        try {
            val failure = error.response()?.errorBody()?.use { Gson().fromJson(it.string(), ChatErrorResponse::class.java) }
            failure?.sessionId?.let { rememberSessionId(it, requestId) }
        } catch (error: JsonParseException) {
            _state.update { it.copy(selectionError = "Chat ID confirm nahi hui. Saved chats reload karke check karo.") }
        } catch (error: IOException) {
            _state.update { it.copy(selectionError = "Chat ID confirm nahi hui. Saved chats reload karke check karo.") }
        }
    }

    fun sendMessage(text: String, base64Image: String? = null) {
        val userId = activeUserId ?: return
        val current = _state.value
        if (!current.hasSelection) {
            _state.update { it.copy(selectionError = "Pehle General ya garage ki bike select kar.") }
            return
        }
        if (!sessionReady || current.loading || text.isBlank()) return
        val request = ChatRequest(current.chatId ?: 0, text, base64Image, current.selectedBike?.id, current.isGeneral)
        val requestGeneration = ++generation
        _state.update { it.copy(selectionError = null, selectionLocked = true, draft = ChatDraft(), loading = true,
            messages = it.messages + ChatMessage(text, true, base64Image != null)) }
        chatJob = viewModelScope.launch {
            var token: String? = null
            try {
                token = auth.accessToken(userId)
                currentCoroutineContext().ensureActive()
                if (!isCurrent(userId, requestGeneration)) return@launch
                val response = api.askMechanicBro(request, "Bearer $token")
                currentCoroutineContext().ensureActive()
                if (!isCurrent(userId, requestGeneration)) return@launch
                rememberSessionId(response.sessionId, request.sessionId)
                _state.update { it.copy(messages = it.messages + ChatMessage(response.reply, false)) }
                refreshRecent()
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                if (error is HttpException && error.code() == 401 && token != null) auth.invalidateSession(userId, token)
                if (!isCurrent(userId, requestGeneration)) return@launch
                if (error is HttpException) rememberFailureSessionId(error, request.sessionId)
                _state.update { it.copy(messages = it.messages + ChatMessage("Bhai, ${errorMessage(error)}", false)) }
                refreshRecent()
            } finally {
                // Old cancelled jobs must not clear a newer request's spinner.
                if (isCurrent(userId, requestGeneration)) _state.update { it.copy(loading = false) }
            }
        }
    }

    private fun errorMessage(error: Exception): String = when {
        error is InterruptedIOException -> "Response time par nahi aaya. Saved chat reload karke check karo before retry."
        error is SessionNotReadyException -> "Session refresh chal raha hai. Thodi der mein retry kar."
        error is HttpException -> when (error.code()) {
            400 -> "Message/photo ya chat selection invalid hai."
            404 -> "Chat ya selected bike available nahi hai. New Chat kholo."
            409 -> "Chat conflict hai. Saved chat reload karo."
            413 -> "Photo/request bahut badi hai."
            422 -> "Tool limit ya verified manual unavailable hai."
            429 -> "Requests ki limit aa gayi. Thoda ruk kar retry karo."
            503 -> "Database/provider unavailable hai. Saved chat check karke retry karo."
            else -> "Answer save nahi hua. Saved chat check karke retry karo."
        }
        else -> "Answer nahi aaya. Network aur saved chat check karo."
    }
}
